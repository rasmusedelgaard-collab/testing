import path from 'node:path';
import express from 'express';
import type { Express } from 'express';
import type session from 'express-session';
import { createDemoAuthRouter } from './auth/demoAuth';
import { createMsalClient, type OidcClient } from './auth/msal';
import { requireAuth, requireRole } from './auth/requireRole';
import { createSession } from './auth/session';
import { createTestLoginRouter } from './auth/testLogin';
import type { AppConfig } from './config';
import { pingDatabase } from './db/publicReads';
import { testUserRepo, type TestUserRepo } from './db/testUserRepo';
import { userRepo, type UserRepo } from './db/userRepo';
import { csrfProtection } from './middleware/csrf';
import { errorHandler, notFoundHandler } from './middleware/errorPages';
import { requestContext } from './middleware/requestContext';
import { securityHeaders } from './middleware/security';
import { createCommonAuthRouter, createEntraAuthRouter } from './routes/auth';
import { createAppRouter } from './routes/app';

export interface AppDeps {
  config: AppConfig;
  oidc?: OidcClient;
  userRepo?: UserRepo;
  testUserRepo?: TestUserRepo;
  sessionStore?: session.Store;
  ping?: () => Promise<boolean>;
}

export function createApp(deps: AppDeps): Express {
  const { config } = deps;
  const app = express();
  app.disable('x-powered-by');
  app.set('trust proxy', 1);
  app.set('view engine', 'ejs');
  app.set('views', path.join(__dirname, 'views'));

  app.use(requestContext);

  // View defaults first, so error pages render even when the session store or database is down.
  const testBanner = !config.demoMode && config.authMode === 'test-access-code';
  app.use((_req, res, next) => {
    res.locals.appName = 'CRM';
    res.locals.testBanner = testBanner;
    res.locals.demoBanner = config.demoMode;
    res.locals.currentUser = undefined;
    res.locals.activeNav = '';
    res.locals.csrfField = () => '';
    next();
  });
  app.use(securityHeaders(config));
  app.use(express.static(path.join(__dirname, 'public'), { maxAge: '1h', index: false }));

  // Public probes: no session, no DB for /healthz.
  app.get('/healthz', (_req, res) => res.type('text/plain').send('ok'));
  app.get('/readyz', async (_req, res) => {
    const ok = await (deps.ping ?? pingDatabase)();
    res.status(ok ? 200 : 503).type('text/plain').send(ok ? 'ok' : 'fail');
  });

  const sessions = createSession(config, deps.sessionStore);
  app.use(sessions.middleware);
  app.use(sessions.cleanup);
  app.use(express.urlencoded({ extended: false, limit: '100kb' }));
  app.use(csrfProtection);

  // Authentication, by mode.
  let loginPath = '/auth/login';
  let oidc: OidcClient | undefined;
  if (config.demoMode) {
    loginPath = '/signin';
    app.use(createDemoAuthRouter({ allowGetLogin: config.nodeEnv === 'test' }));
  } else if (config.authMode === 'test-access-code') {
    loginPath = '/auth/test-login';
    app.use(createTestLoginRouter({ accessCode: config.testAccessCode ?? '', repo: deps.testUserRepo ?? testUserRepo }));
  } else {
    oidc = deps.oidc ?? createMsalClient(config);
    app.use(createEntraAuthRouter({ config, oidc, users: deps.userRepo ?? userRepo }));
  }
  app.use(createCommonAuthRouter({ config, oidc, loginPath }));

  // Everything below needs a signed-in user.
  const authed = express.Router();
  authed.use(requireAuth({ absoluteHours: config.sessionAbsoluteHours }));
  authed.use(createAppRouter({ requireAdmin: requireRole('Admin') }));
  app.use(authed);

  app.use(notFoundHandler);
  app.use(errorHandler);
  return app;
}
