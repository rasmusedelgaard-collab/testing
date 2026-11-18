import { describe, expect, it } from 'vitest';
import { ConfigError, loadConfig } from '../src/config';

const GUID = '11111111-2222-4333-8444-555555555555';
const secret = 'x'.repeat(32);

const entraEnv = {
  NODE_ENV: 'production',
  ENTRA_TENANT_ID: GUID,
  ENTRA_CLIENT_ID: GUID,
  ENTRA_CLIENT_SECRET: 's3cret',
  REDIRECT_URI: 'https://crm.example.com/auth/redirect',
  SESSION_SECRET: secret,
  DB_SERVER: 'sql.example.com',
  DB_NAME: 'crm',
  DB_AUTH_MODE: 'managed-identity',
};

const issues = (env: NodeJS.ProcessEnv): string => {
  try {
    loadConfig(env);
  } catch (e) {
    expect(e).toBeInstanceOf(ConfigError);
    return (e as ConfigError).message;
  }
  return '';
};

describe('config: entra mode', () => {
  it('accepts a complete production config', () => {
    const c = loadConfig(entraEnv);
    expect(c.authMode).toBe('entra');
    expect(c.demoMode).toBe(false);
    expect(c.secureCookies).toBe(true);
    expect(c.baseUrl).toBe('https://crm.example.com');
    expect(c.db?.authMode).toBe('managed-identity');
    expect(c.sessionIdleMinutes).toBe(60);
  });

  it.each(['ENTRA_TENANT_ID', 'ENTRA_CLIENT_ID', 'ENTRA_CLIENT_SECRET', 'SESSION_SECRET', 'REDIRECT_URI', 'DB_SERVER', 'DB_NAME', 'DB_AUTH_MODE'])('requires %s', (key) => {
    const env: NodeJS.ProcessEnv = { ...entraEnv };
    delete env[key];
    expect(issues(env)).toContain(key);
  });

  it('rejects a short session secret and non-https redirect in production', () => {
    expect(issues({ ...entraEnv, SESSION_SECRET: 'short' })).toContain('SESSION_SECRET');
    expect(issues({ ...entraEnv, REDIRECT_URI: 'http://crm.example.com/auth/redirect' })).toContain('https');
  });

  it('supports a comma-separated secret list for rotation', () => {
    const c = loadConfig({ ...entraEnv, SESSION_SECRET: `${secret},${'y'.repeat(40)}` });
    expect(c.sessionSecrets).toHaveLength(2);
  });
});

describe('config: DB_AUTH_MODE', () => {
  it('requires user and password for sql-password', () => {
    const m = issues({ ...entraEnv, DB_AUTH_MODE: 'sql-password' });
    expect(m).toContain('DB_USER');
    expect(m).toContain('DB_PASSWORD');
    expect(loadConfig({ ...entraEnv, DB_AUTH_MODE: 'sql-password', DB_USER: 'u', DB_PASSWORD: 'p' }).db?.user).toBe('u');
  });
  it('rejects DB_USER/DB_PASSWORD for the other modes', () => {
    expect(issues({ ...entraEnv, DB_USER: 'u' })).toContain('only allowed when DB_AUTH_MODE=sql-password');
    expect(issues({ ...entraEnv, DB_AUTH_MODE: 'default-azure', DB_PASSWORD: 'p' })).toContain('DB_PASSWORD');
  });
  it('rejects unknown modes', () => {
    expect(issues({ ...entraEnv, DB_AUTH_MODE: 'kerberos' })).toContain('DB_AUTH_MODE');
  });
});

describe('config: PORT', () => {
  it('keeps a numeric port as a string and accepts an iisnode named pipe untouched', () => {
    expect(loadConfig({ ...entraEnv, PORT: '8080' }).port).toBe('8080');
    const pipe = '\\\\.\\pipe\\1234abcd-5678';
    expect(loadConfig({ ...entraEnv, PORT: pipe }).port).toBe(pipe);
  });
  it('rejects garbage', () => {
    expect(issues({ ...entraEnv, PORT: 'abc' })).toContain('PORT');
  });
});

describe('config: test-access-code mode', () => {
  const base = {
    NODE_ENV: 'production',
    AUTH_MODE: 'test-access-code',
    REDIRECT_URI: 'https://crmtest.example.com/auth/redirect',
    TEST_ACCESS_CODE: 'a-long-shared-code-1234-xyz',
    SESSION_SECRET: secret,
    DB_SERVER: 'db.example.com',
    DB_NAME: 'db1',
    DB_AUTH_MODE: 'sql-password',
    DB_USER: 'owner',
    DB_PASSWORD: 'pw',
  };
  it('does not need Entra variables', () => {
    const c = loadConfig(base);
    expect(c.authMode).toBe('test-access-code');
    expect(c.testAccessCode).toBe('a-long-shared-code-1234-xyz');
    expect(c.secureCookies).toBe(true);
  });
  it('requires a TEST_ACCESS_CODE of at least 16 characters and SESSION_SECRET', () => {
    expect(issues({ ...base, TEST_ACCESS_CODE: 'short' })).toContain('TEST_ACCESS_CODE');
    const noCode: NodeJS.ProcessEnv = { ...base };
    delete noCode.TEST_ACCESS_CODE;
    expect(issues(noCode)).toContain('TEST_ACCESS_CODE');
    const noSecret: NodeJS.ProcessEnv = { ...base };
    delete noSecret.SESSION_SECRET;
    expect(issues(noSecret)).toContain('SESSION_SECRET');
  });
  it('still needs the database settings', () => {
    const env: NodeJS.ProcessEnv = { ...base };
    delete env.DB_SERVER;
    expect(issues(env)).toContain('DB_SERVER');
  });
  it('is never active outside this mode: entra mode ignores TEST_ACCESS_CODE', () => {
    expect(loadConfig({ ...entraEnv, TEST_ACCESS_CODE: 'a-long-shared-code-1234-xyz' }).testAccessCode).toBeNull();
  });
});

describe('config: demo mode', () => {
  it('needs nothing in NODE_ENV=test and generates an ephemeral session secret', () => {
    const c = loadConfig({ NODE_ENV: 'test' });
    expect(c.demoMode).toBe(true);
    expect(c.db).toBeNull();
    expect(c.sessionSecrets[0]).toHaveLength(64);
  });
  it('DEMO_MODE=true works in development', () => {
    expect(loadConfig({ NODE_ENV: 'development', DEMO_MODE: 'true' }).demoMode).toBe(true);
  });
  it('refuses DEMO_MODE=true in production', () => {
    expect(issues({ ...entraEnv, DEMO_MODE: 'true' })).toContain('DEMO_MODE=true is refused');
  });
  it('refuses DEMO_MODE and NODE_ENV=test on Azure App Service', () => {
    expect(issues({ NODE_ENV: 'development', DEMO_MODE: 'true', WEBSITE_SITE_NAME: 'x' })).toContain('refused');
    expect(issues({ NODE_ENV: 'test', WEBSITE_SITE_NAME: 'x' })).toContain('refused');
  });
});

describe('config: live-environment refusals and hardening', () => {
  const testMode = {
    NODE_ENV: 'production', AUTH_MODE: 'test-access-code', REDIRECT_URI: 'https://crmtest.example.com/auth/redirect',
    TEST_ACCESS_CODE: 'a-long-shared-code-1234-xyz', SESSION_SECRET: secret, DB_SERVER: 'db', DB_NAME: 'd',
    DB_AUTH_MODE: 'sql-password', DB_USER: 'u', DB_PASSWORD: 'p',
  };
  it('test-access-code starts normally on a non-Azure host (e.g. MonsterASP), even if WEBSITE_SITE_NAME is present', () => {
    expect(loadConfig(testMode).authMode).toBe('test-access-code');
    expect(loadConfig({ ...testMode, WEBSITE_SITE_NAME: 'x' }).authMode).toBe('test-access-code');
  });
  it.each([{ LIVE: 'true' }, { WEBSITE_INSTANCE_ID: 'abc' }])('refuses test-access-code when live: %j', (extra) => {
    expect(() => loadConfig({ ...testMode, ...extra })).toThrow(/test-access-code is refused/);
  });
  it('refuses test-access-code when an Entra client secret is present', () => {
    expect(() => loadConfig({ ...testMode, ENTRA_CLIENT_SECRET: 's' })).toThrow(/ENTRA_CLIENT_SECRET/);
  });
  it.each([{ LIVE: 'true' }, { WEBSITE_INSTANCE_ID: 'abc' }])('refuses DB_AUTH_MODE=sql-password when live: %j', (extra) => {
    expect(() => loadConfig({ ...entraEnv, DB_AUTH_MODE: 'sql-password', DB_USER: 'u', DB_PASSWORD: 'p', ...extra })).toThrow(/sql-password is refused/);
  });
  it('allows sql-password when not live, and managed-identity when live', () => {
    expect(loadConfig({ ...entraEnv, DB_AUTH_MODE: 'sql-password', DB_USER: 'u', DB_PASSWORD: 'p' }).db?.authMode).toBe('sql-password');
    expect(loadConfig({ ...entraEnv, LIVE: 'true' }).db?.authMode).toBe('managed-identity');
  });
  it('production test-access-code needs an https REDIRECT_URI, which turns on secure cookies', () => {
    const noUri: NodeJS.ProcessEnv = { ...testMode };
    delete noUri.REDIRECT_URI;
    expect(() => loadConfig(noUri)).toThrow(/REDIRECT_URI/);
    expect(() => loadConfig({ ...testMode, REDIRECT_URI: 'http://x.example.com/auth/redirect' })).toThrow(/https/);
    expect(loadConfig(testMode).secureCookies).toBe(true);
  });
  it.each([
    [{ DEMO_MODE: 'true', AUTH_MODE: 'test-access-code' }],
    [{ DEMO_MODE: 'true', LIVE: 'true' }],
    [{ DEMO_MODE: 'true', DB_SERVER: 'x' }],
    [{ DEMO_MODE: 'true', WEBSITE_SITE_NAME: 'x' }],
  ])('demo mode is refused next to real-environment settings: %j', (extra) => {
    expect(() => loadConfig({ NODE_ENV: 'development', ...extra })).toThrow(/DEMO_MODE=true is refused/);
  });
  it('rejects repetitive or short access codes', () => {
    expect(() => loadConfig({ ...testMode, TEST_ACCESS_CODE: 'abababababababababababab' })).toThrow(/repetitive/);
    expect(() => loadConfig({ ...testMode, TEST_ACCESS_CODE: 'short-but-varied-123' })).toThrow(/24/);
  });
  it('PORT must be 1-65535 (named pipes still allowed)', () => {
    for (const bad of ['0', '65536', '99999']) expect(() => loadConfig({ ...entraEnv, PORT: bad })).toThrow(/PORT/);
    expect(loadConfig({ ...entraEnv, PORT: '65535' }).port).toBe('65535');
  });
  it('TRUST_PROXY defaults to 1 behind https and 0 otherwise, and is configurable', () => {
    expect(loadConfig(entraEnv).trustProxy).toBe(1);
    expect(loadConfig({ ...entraEnv, TRUST_PROXY: '0' }).trustProxy).toBe(0);
    expect(loadConfig({ NODE_ENV: 'test' }).trustProxy).toBe(0);
  });
});
