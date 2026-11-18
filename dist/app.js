"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.createApp = createApp;
const node_path_1 = __importDefault(require("node:path"));
const express_1 = __importDefault(require("express"));
const demoAuth_1 = require("./auth/demoAuth");
const msal_1 = require("./auth/msal");
const requireRole_1 = require("./auth/requireRole");
const session_1 = require("./auth/session");
const testLogin_1 = require("./auth/testLogin");
const publicReads_1 = require("./db/publicReads");
const testUserRepo_1 = require("./db/testUserRepo");
const userRepo_1 = require("./db/userRepo");
const csrf_1 = require("./middleware/csrf");
const errorPages_1 = require("./middleware/errorPages");
const rateLimit_1 = require("./middleware/rateLimit");
const requestContext_1 = require("./middleware/requestContext");
const security_1 = require("./middleware/security");
const auth_1 = require("./routes/auth");
const app_1 = require("./routes/app");
const crm_1 = require("./routes/crm");
const dashboard_1 = require("./routes/dashboard");
const settings_1 = require("./routes/settings");
const branding_1 = require("./middleware/branding");
const publicReads_2 = require("./db/publicReads");
const memoryStore_1 = require("./db/memoryStore");
const memoryStore_2 = require("./db/memoryStore");
const sqlRepo_1 = require("./db/sqlRepo");
function createApp(deps) {
    const { config } = deps;
    const app = (0, express_1.default)();
    app.disable('x-powered-by');
    app.set('trust proxy', config.trustProxy);
    app.set('view engine', 'ejs');
    app.set('views', node_path_1.default.join(__dirname, 'views'));
    app.use(requestContext_1.requestContext);
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
    app.use((0, security_1.securityHeaders)(config));
    app.use(express_1.default.static(node_path_1.default.join(__dirname, 'public'), { maxAge: '1h', index: false }));
    // Public probes: no session, no DB for /healthz.
    app.get('/healthz', (_req, res) => res.type('text/plain').send('ok'));
    // Probe results are cached for 10 s and the endpoint is rate limited, so it cannot hammer (or keep awake) the database.
    const probe = (0, rateLimit_1.cachedProbe)(deps.ping ?? publicReads_1.pingDatabase, 10_000);
    app.get('/readyz', (0, rateLimit_1.rateLimit)({ max: 30, windowMs: 60_000 }), async (_req, res) => {
        if (config.demoMode && !deps.ping)
            return res.json({ status: 'ok', db: 'demo' }); // no database in demo mode
        const ok = await probe();
        res.status(ok ? 200 : 503).type('text/plain').send(ok ? 'ok' : 'fail');
    });
    const store = deps.store ?? (config.demoMode ? (0, memoryStore_2.createMemoryStore)() : sqlRepo_1.sqlStore);
    const branding = deps.branding ?? ('state' in store ? (0, memoryStore_1.memoryBranding)(store.state) : (0, publicReads_2.createSqlBranding)());
    app.use((0, branding_1.createBrandingRouter)(branding)); // public: theme.css, logo, and the appName/logo locals for every page
    const sessions = (0, session_1.createSession)(config, deps.sessionStore);
    app.use(sessions.middleware);
    app.use(sessions.cleanup);
    app.use(express_1.default.urlencoded({ extended: false, limit: '100kb' }));
    app.use(csrf_1.csrfProtection);
    app.use('/auth', (0, rateLimit_1.rateLimit)({ max: 120, windowMs: 60_000 }));
    // Authentication, by mode.
    let loginPath = '/auth/login';
    let oidc;
    if (config.demoMode) {
        loginPath = '/signin';
        app.use((0, demoAuth_1.createDemoAuthRouter)({ allowGetLogin: config.nodeEnv === 'test' }));
    }
    else if (config.authMode === 'test-access-code') {
        loginPath = '/auth/test-login';
        app.use((0, testLogin_1.createTestLoginRouter)({ accessCode: config.testAccessCode ?? '', repo: deps.testUserRepo ?? testUserRepo_1.testUserRepo }));
    }
    else {
        oidc = deps.oidc ?? (0, msal_1.createMsalClient)(config);
        app.use((0, auth_1.createEntraAuthRouter)({ config, oidc, users: deps.userRepo ?? userRepo_1.userRepo }));
    }
    app.use((0, auth_1.createCommonAuthRouter)({ config, oidc, loginPath }));
    // Everything below needs a signed-in user.
    const authed = express_1.default.Router();
    // Authenticated pages carry personal data: never cache them in the browser or a proxy.
    authed.use((_req, res, next) => {
        res.set('Cache-Control', 'no-store');
        next();
    });
    authed.use((0, requireRole_1.requireAuth)({ absoluteHours: config.sessionAbsoluteHours, isActive: config.demoMode ? undefined : (deps.isUserActive ?? userRepo_1.userRepo.isActive), recheckMs: 5 * 60_000 }));
    authed.use((req, res, next) => {
        const f = req.session.flash;
        if (f) {
            res.locals.flash = f;
            delete req.session.flash;
        }
        next();
    });
    authed.use('/settings', (0, requireRole_1.requireRole)('Admin'), (0, settings_1.createSettingsRouter)(store, branding));
    authed.use((0, dashboard_1.createDashboardRouter)(store));
    authed.use((0, crm_1.createCrmRouter)(store));
    authed.use((0, app_1.createAppRouter)());
    app.use(authed);
    app.use(errorPages_1.notFoundHandler);
    app.use(errorPages_1.errorHandler);
    return app;
}
//# sourceMappingURL=app.js.map