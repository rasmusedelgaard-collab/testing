import session from 'express-session';
import request from 'supertest';
import { createApp, type AppDeps } from '../src/app';
import { loadConfig } from '../src/config';

export const GUID = '11111111-2222-4333-8444-555555555555';

export function demoConfig(extra: NodeJS.ProcessEnv = {}) {
  return loadConfig({ NODE_ENV: 'test', ...extra });
}

export function entraConfig(extra: NodeJS.ProcessEnv = {}) {
  return loadConfig({
    NODE_ENV: 'development', // not production: plain-http cookies so supertest can carry them
    ENTRA_TENANT_ID: GUID,
    ENTRA_CLIENT_ID: GUID,
    ENTRA_CLIENT_SECRET: 's3cret',
    REDIRECT_URI: 'http://localhost:3000/auth/redirect',
    SESSION_SECRET: 'z'.repeat(40),
    DB_SERVER: 'sql.example.com',
    DB_NAME: 'crm',
    DB_AUTH_MODE: 'default-azure',
    ...extra,
  });
}

export function testAccessConfig(extra: NodeJS.ProcessEnv = {}) {
  return loadConfig({
    NODE_ENV: 'development',
    AUTH_MODE: 'test-access-code',
    TEST_ACCESS_CODE: 'correct-horse-battery-staple',
    SESSION_SECRET: 'z'.repeat(40),
    DB_SERVER: 'db.example.com',
    DB_NAME: 'db1',
    DB_AUTH_MODE: 'sql-password',
    DB_USER: 'owner',
    DB_PASSWORD: 'pw',
    ...extra,
  });
}

export function makeApp(deps: Omit<AppDeps, 'sessionStore'> & { sessionStore?: session.MemoryStore }) {
  const store = deps.sessionStore ?? new session.MemoryStore();
  return { app: createApp({ ...deps, sessionStore: store }), store };
}

export function csrfFrom(html: string): string {
  const m = /name="_csrf" value="([^"]+)"/.exec(html);
  if (!m) throw new Error('no csrf token in page');
  return m[1] as string;
}

export async function getCsrf(agent: ReturnType<typeof request.agent>, path: string): Promise<string> {
  const res = await agent.get(path);
  return csrfFrom(res.text);
}

export function allSessions(store: session.MemoryStore): Promise<Record<string, session.SessionData>> {
  return new Promise((resolve, reject) =>
    store.all((err, sessions) => (err ? reject(err as Error) : resolve((sessions ?? {}) as Record<string, session.SessionData>))),
  );
}
