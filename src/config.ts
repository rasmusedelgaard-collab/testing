import { randomBytes } from 'node:crypto';
import { z } from 'zod';

/**
 * The only place that reads process.env (besides scripts/ and the PORT lookup in server.ts via config).
 * Validation happens once at startup and fails fast with a readable message.
 */

const guid = z.string().regex(/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/, 'must be a GUID');
const bool = z.enum(['true', 'false']).transform((v) => v === 'true');
const intInRange = (min: number, max: number) =>
  z.coerce.number().int().min(min).max(max);

// PORT is deliberately NOT coerced to a number: iisnode passes a named pipe such as \\.\pipe\<guid>.
const portOrPipe = z
  .string()
  .regex(/^(\d{1,5}|\\\\[.?]\\pipe\\.+)$/, 'must be a port number or a Windows named pipe');

export const DB_AUTH_MODES = ['managed-identity', 'default-azure', 'sql-password'] as const;
export type DbAuthMode = (typeof DB_AUTH_MODES)[number];

const dbSchema = z
  .object({
    DB_SERVER: z.string().min(1),
    DB_NAME: z.string().min(1),
    DB_AUTH_MODE: z.enum(DB_AUTH_MODES),
    DB_USER: z.string().min(1).optional(),
    DB_PASSWORD: z.string().min(1).optional(),
    DB_TRUST_SERVER_CERT: bool.default(false),
    SQL_POOL_MAX: intInRange(1, 100).default(10),
  })
  .superRefine((v, ctx) => {
    if (v.DB_AUTH_MODE === 'sql-password') {
      if (!v.DB_USER) ctx.addIssue({ code: 'custom', path: ['DB_USER'], message: 'required when DB_AUTH_MODE=sql-password' });
      if (!v.DB_PASSWORD) ctx.addIssue({ code: 'custom', path: ['DB_PASSWORD'], message: 'required when DB_AUTH_MODE=sql-password' });
    } else {
      if (v.DB_USER) ctx.addIssue({ code: 'custom', path: ['DB_USER'], message: 'only allowed when DB_AUTH_MODE=sql-password' });
      if (v.DB_PASSWORD) ctx.addIssue({ code: 'custom', path: ['DB_PASSWORD'], message: 'only allowed when DB_AUTH_MODE=sql-password' });
    }
  });

export interface DbConfig {
  server: string;
  database: string;
  authMode: DbAuthMode;
  user?: string;
  password?: string;
  trustServerCertificate: boolean;
  poolMax: number;
}

export interface AppConfig {
  nodeEnv: 'development' | 'test' | 'production';
  isProduction: boolean;
  /** True when running without Entra and SQL (NODE_ENV=test or DEMO_MODE=true). */
  demoMode: boolean;
  port: string;
  logLevel: string;
  redirectUri: string;
  /** Origin of REDIRECT_URI, used for the post-logout redirect. */
  baseUrl: string;
  /** True when the site is served over https: drives cookie flags and HSTS. */
  secureCookies: boolean;
  entra: { tenantId: string; clientId: string; clientSecret: string };
  sessionSecrets: string[];
  sessionIdleMinutes: number;
  sessionAbsoluteHours: number;
  db: DbConfig | null;
}

const baseSchema = z.object({
  NODE_ENV: z.enum(['development', 'test', 'production']).default('development'),
  PORT: portOrPipe.default('3000'),
  LOG_LEVEL: z.enum(['fatal', 'error', 'warn', 'info', 'debug', 'trace', 'silent']).default('info'),
  DEMO_MODE: bool.default(false),
  REDIRECT_URI: z.url().optional(),
  ENTRA_TENANT_ID: guid.optional(),
  ENTRA_CLIENT_ID: guid.optional(),
  ENTRA_CLIENT_SECRET: z.string().min(1).optional(),
  SESSION_SECRET: z.string().optional(),
  SESSION_IDLE_MINUTES: intInRange(1, 1440).default(60),
  SESSION_ABSOLUTE_HOURS: intInRange(1, 168).default(10),
});

export class ConfigError extends Error {
  constructor(public readonly issues: string[]) {
    super(`Invalid configuration:\n${issues.map((i) => `  - ${i}`).join('\n')}`);
    this.name = 'ConfigError';
  }
}

function formatIssues(err: z.ZodError): string[] {
  return err.issues.map((i) => `${i.path.join('.') || '(root)'}: ${i.message}`);
}

/** Validates only the database variables. Used by scripts/migrate.ts as well. */
export function loadDbConfig(env: NodeJS.ProcessEnv): DbConfig {
  const parsed = dbSchema.safeParse(env);
  if (!parsed.success) throw new ConfigError(formatIssues(parsed.error));
  const v = parsed.data;
  return {
    server: v.DB_SERVER,
    database: v.DB_NAME,
    authMode: v.DB_AUTH_MODE,
    user: v.DB_USER,
    password: v.DB_PASSWORD,
    trustServerCertificate: v.DB_TRUST_SERVER_CERT,
    poolMax: v.SQL_POOL_MAX,
  };
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): AppConfig {
  const base = baseSchema.safeParse(env);
  if (!base.success) throw new ConfigError(formatIssues(base.error));
  const v = base.data;
  const issues: string[] = [];

  const isProduction = v.NODE_ENV === 'production';
  const demoMode = v.NODE_ENV === 'test' || v.DEMO_MODE;

  // The bypass must never be reachable in production or on Azure App Service.
  if (v.DEMO_MODE && (isProduction || env.WEBSITE_SITE_NAME)) {
    throw new ConfigError(['DEMO_MODE=true is refused when NODE_ENV=production or on Azure App Service']);
  }
  if (v.NODE_ENV === 'test' && env.WEBSITE_SITE_NAME) {
    throw new ConfigError(['NODE_ENV=test (auth bypass) is refused on Azure App Service']);
  }

  const redirectUri = v.REDIRECT_URI ?? (demoMode ? `http://localhost:${/^\d+$/.test(v.PORT) ? v.PORT : '3000'}/auth/redirect` : undefined);
  if (!redirectUri) issues.push('REDIRECT_URI: required');
  let baseUrl = '';
  let secureCookies = false;
  if (redirectUri) {
    const url = new URL(redirectUri);
    baseUrl = url.origin;
    secureCookies = url.protocol === 'https:';
    if (isProduction && !secureCookies) issues.push('REDIRECT_URI: must use https in production');
  }

  let sessionSecrets: string[];
  if (v.SESSION_SECRET) {
    sessionSecrets = v.SESSION_SECRET.split(',').map((s) => s.trim()).filter(Boolean);
    if (sessionSecrets.length === 0 || sessionSecrets.some((s) => s.length < 32)) {
      issues.push('SESSION_SECRET: each secret (comma-separated for rotation) must be at least 32 characters');
    }
  } else if (demoMode) {
    sessionSecrets = [randomBytes(32).toString('hex')]; // ephemeral: demo sessions die with the process
  } else {
    sessionSecrets = [];
    issues.push('SESSION_SECRET: required');
  }

  let db: DbConfig | null = null;
  if (!demoMode) {
    for (const key of ['ENTRA_TENANT_ID', 'ENTRA_CLIENT_ID', 'ENTRA_CLIENT_SECRET'] as const) {
      if (!v[key]) issues.push(`${key}: required`);
    }
    const parsedDb = dbSchema.safeParse(env);
    if (parsedDb.success) db = loadDbConfig(env);
    else issues.push(...formatIssues(parsedDb.error));
  }

  if (issues.length > 0) throw new ConfigError(issues);

  return {
    nodeEnv: v.NODE_ENV,
    isProduction,
    demoMode,
    port: v.PORT,
    logLevel: v.LOG_LEVEL,
    redirectUri: redirectUri as string,
    baseUrl,
    secureCookies,
    entra: {
      tenantId: v.ENTRA_TENANT_ID ?? '',
      clientId: v.ENTRA_CLIENT_ID ?? '',
      clientSecret: v.ENTRA_CLIENT_SECRET ?? '',
    },
    sessionSecrets,
    sessionIdleMinutes: v.SESSION_IDLE_MINUTES,
    sessionAbsoluteHours: v.SESSION_ABSOLUTE_HOURS,
    db,
  };
}

let cached: AppConfig | undefined;
export function getConfig(): AppConfig {
  cached ??= loadConfig(process.env);
  return cached;
}
