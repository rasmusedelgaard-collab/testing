"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.cookieName = cookieName;
exports.createSession = createSession;
const express_session_1 = __importDefault(require("express-session"));
const sessionRepo_1 = require("../db/sessionRepo");
const sessionStore_1 = require("./sessionStore");
function cookieName(config) {
    // The __Host- prefix requires Secure, so it is only used when the site is served over https.
    return config.secureCookies ? '__Host-crm.sid' : 'crm.sid';
}
function createSession(config, store) {
    // MemoryStore only for demo/test (config forbids demo mode in production); otherwise SQL.
    const sessionStore = store ?? (config.demoMode ? new express_session_1.default.MemoryStore() : new sessionStore_1.SqlSessionStore(sessionRepo_1.sessionRepo, config.sessionIdleMinutes));
    const middleware = (0, express_session_1.default)({
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
    const cleanup = (_req, res, next) => {
        if (sessionStore instanceof sessionStore_1.SqlSessionStore)
            res.on('finish', () => sessionStore.maybeCleanup());
        next();
    };
    return { middleware, cleanup };
}
//# sourceMappingURL=session.js.map