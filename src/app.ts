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
import { cachedProbe, rateLimit } from './middleware/rateLimit';
import { requestContext } from './middleware/requestContext';
import { securityHeaders } from './middleware/security';
import { createCommonAuthRouter, createEntraAuthRouter } from './routes/auth';
import { createAppRouter } from './routes/app';
import { createCrmRouter } from './routes/crm';
import { createDashboardRouter } from './routes/dashboard';
import { createSettingsRouter } from './routes/settings';
import { createBrandingRouter } from './middleware/branding';
import { createSqlBranding } from './db/publicReads';
import { memoryBranding } from './db/memoryStore';
import type { BrandingProvider } from './services/brandingTypes';
import { createMemoryStore } from './db/memoryStore';
import { sqlStore } from './db/sqlRepo';
import type { CrmStore } from './domain/types';

export interface AppDeps {
  config: AppConfig;
  oidc?: OidcClient;
  userRepo?: UserRepo;
  testUserRepo?: TestUserRepo;
  sessionStore?: session.Store;
  ping?: () => Promise<boolean>;
  store?: CrmStore;
  branding?: BrandingProvider;
  isUserActive?: (userId: number) => Promise<boolean>;
}

export function createApp(deps: AppDeps): Express {
  const { config } = deps;
  const app = express();
  app.disable('x-powered-by');
  app.set('trust proxy', config.trustProxy);
  app.set('view engine', 'ejs');
  app.set('views', path.join(__dirname, 'views'));

  app.use(requestContext);

  // View defaults first, so error pages render even when the session store or database is down.
  const testBanner = !config.demoMode && config.authMode === 'test-access-code';
  app.use((_req, res, next) => {
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
  // Probe results are cached for 10 s and the endpoint is rate limited, so it cannot hammer (or keep awake) the database.
  const probe = cachedProbe(deps.ping ?? pingDatabase, 10_000);
  app.get('/readyz', rateLimit({ max: 30, windowMs: 60_000 }), async (_req, res) => {
    if (config.demoMode && !deps.ping) return res.json({ status: 'ok', db: 'demo' }); // no database in demo mode
    const ok = await probe();
    res.status(ok ? 200 : 503).type('text/plain').send(ok ? 'ok' : 'fail');
  });

  const store: CrmStore = deps.store ?? (config.demoMode ? createMemoryStore() : sqlStore);
  const branding: BrandingProvider = deps.branding ?? ('state' in store ? memoryBranding((store as ReturnType<typeof createMemoryStore>).state) : createSqlBranding());
  app.use(createBrandingRouter(branding)); // public: theme.css, logo, and the appName/logo locals for every page

  const sessions = createSession(config, deps.sessionStore);
  app.use(sessions.middleware);
  app.use(sessions.cleanup);
  app.use(express.urlencoded({ extended: false, limit: '100kb' }));
  app.use(csrfProtection);

  app.use('/auth', rateLimit({ max: 120, windowMs: 60_000 }));

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
  // Authenticated pages carry personal data: never cache them in the browser or a proxy.
  authed.use((_req, res, next) => {
    res.set('Cache-Control', 'no-store');
    next();
  });
  authed.use(requireAuth({ absoluteHours: config.sessionAbsoluteHours, isActive: config.demoMode ? undefined : (deps.isUserActive ?? userRepo.isActive), recheckMs: 5 * 60_000 }));
  authed.use((req, res, next) => {
    const f = req.session.flash;
    if (f) {
      res.locals.flash = f;
      delete req.session.flash;
    }
    next();
  });
  authed.use('/settings', requireRole('Admin'), createSettingsRouter(store, branding));
  authed.use(createDashboardRouter(store));
  authed.use(createCrmRouter(store));
  authed.use(createAppRouter());
  app.use(authed);

  app.use(notFoundHandler);
  app.use(errorHandler);
  return app;
}
