import type { RequestHandler } from 'express';
import session from 'express-session';
import type { AppConfig } from '../config';
import { sessionRepo } from '../db/sessionRepo';
import { SqlSessionStore } from './sessionStore';

export interface SessionSetup {
  middleware: RequestHandler;
  cleanup: RequestHandler;
}

export function cookieName(config: AppConfig): string {
  // The __Host- prefix requires Secure, so it is only used when the site is served over https.
  return config.secureCookies ? '__Host-crm.sid' : 'crm.sid';
}

export function createSession(config: AppConfig, store?: session.Store): SessionSetup {
  // MemoryStore only for demo/test (config forbids demo mode in production); otherwise SQL.
  const sessionStore = store ?? (config.demoMode ? new session.MemoryStore() : new SqlSessionStore(sessionRepo, config.sessionIdleMinutes));
  const middleware = session({
    name: cookieName(config),
    secret: config.sessionSecrets,
    store: sessionStore,
    resave: false,
    saveUninitialized: false,
    rolling: true,
    cookie: {
      secure: config.secureCookies,
      httpOnly: true,
      sameSite: 'lax',
      path: '/',
      maxAge: config.sessionIdleMinutes * 60_000,
    },
  });
  const cleanup: RequestHandler = (_req, res, next) => {
    if (sessionStore instanceof SqlSessionStore) res.on('finish', () => sessionStore.maybeCleanup());
    next();
  };
  return { middleware, cleanup };
}
