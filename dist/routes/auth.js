"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.createEntraAuthRouter = createEntraAuthRouter;
exports.createCommonAuthRouter = createCommonAuthRouter;
const node_crypto_1 = require("node:crypto");
const express_1 = require("express");
const establishSession_1 = require("../auth/establishSession");
const roles_1 = require("../auth/roles");
const returnTo_1 = require("../auth/returnTo");
const userSync_1 = require("../auth/userSync");
const transient_1 = require("../db/transient");
const userRepo_1 = require("../db/userRepo");
const logger_1 = require("../logger");
const b64url = (buf) => buf.toString('base64url');
function equalSecrets(a, b) {
    const x = Buffer.from(a);
    const y = Buffer.from(b);
    return x.length === y.length && (0, node_crypto_1.timingSafeEqual)(x, y);
}
const saveSession = (req) => new Promise((resolve, reject) => req.session.save((e) => (e ? reject(e) : resolve())));
function fail(req, res, status, why) {
    logger_1.logger.warn({ reqId: req.id, why }, 'sign-in failed');
    res.status(status).render('pages/signin-error', { title: 'Sign-in problem', reference: req.id });
}
/** Entra auth-code + PKCE flow (ADR-0003). Mounted only when AUTH_MODE=entra and not in demo mode. */
function createEntraAuthRouter(deps) {
    const { config, oidc, users } = deps;
    const router = (0, express_1.Router)();
    router.get('/auth/login', async (req, res) => {
        const verifier = b64url((0, node_crypto_1.randomBytes)(32));
        const state = b64url((0, node_crypto_1.randomBytes)(32));
        const nonce = b64url((0, node_crypto_1.randomBytes)(32));
        const challenge = b64url((0, node_crypto_1.createHash)('sha256').update(verifier).digest());
        req.session.auth = { state, nonce, pkceVerifier: verifier, returnTo: (0, returnTo_1.safeReturnTo)(req.query.returnTo) };
        await saveSession(req);
        res.redirect(302, await oidc.authUrl({ state, nonce, codeChallenge: challenge }));
    });
    router.get('/auth/redirect', async (req, res) => {
        const pending = req.session.auth;
        delete req.session.auth; // single use, whatever happens next
        const { code, state, error } = req.query;
        if (!pending)
            return fail(req, res, 400, 'no pending login');
        if (typeof error === 'string')
            return fail(req, res, 400, `idp error ${error.slice(0, 40)}`);
        if (typeof code !== 'string' || typeof state !== 'string' || !equalSecrets(state, pending.state)) {
            return fail(req, res, 400, 'state mismatch');
        }
        let claims;
        try {
            claims = await oidc.redeem({ code, codeVerifier: pending.pkceVerifier });
        }
        catch (err) {
            logger_1.logger.warn({ reqId: req.id, err: err.message }, 'code redemption failed');
            return fail(req, res, 502, 'redeem failed');
        }
        if (claims.tid.toLowerCase() !== config.entra.tenantId.toLowerCase())
            return fail(req, res, 403, 'tenant mismatch');
        if (!claims.nonce || !equalSecrets(claims.nonce, pending.nonce))
            return fail(req, res, 400, 'nonce mismatch');
        const role = (0, roles_1.mapRole)(claims.roles);
        if (!role) {
            logger_1.logger.info({ oid: claims.oid }, 'sign-in without a CRM app role');
            await saveSession(req);
            return res.redirect(303, '/no-access');
        }
        let user;
        try {
            user = await (0, userSync_1.syncUser)(users, { claims, role, tenantId: claims.tid });
        }
        catch (err) {
            if ((0, transient_1.sqlErrorNumber)(err) === userRepo_1.USER_DEACTIVATED) {
                logger_1.logger.warn({ oid: claims.oid }, 'sign-in refused: user is deactivated');
                await saveSession(req);
                return res.redirect(303, '/no-access');
            }
            throw err;
        }
        await (0, establishSession_1.establishSession)(req, user);
        res.redirect(303, pending.returnTo);
    });
    return router;
}
/** Shared by all auth modes. */
function createCommonAuthRouter(deps) {
    const { config, oidc, loginPath } = deps;
    const router = (0, express_1.Router)();
    router.get('/signin', (req, res) => {
        res.render('pages/signin', {
            title: 'Sign in',
            returnTo: (0, returnTo_1.safeReturnTo)(req.query.returnTo),
            reason: req.query.reason === 'expired' ? 'expired' : null,
            loginPath,
            demo: config.demoMode,
        });
    });
    router.get('/signed-out', (_req, res) => res.render('pages/signed-out', { title: 'Signed out' }));
    router.get('/no-access', (_req, res) => res.status(403).render('pages/no-access', { title: 'No access' }));
    router.post('/auth/signout', async (req, res) => {
        await (0, establishSession_1.destroySession)(req);
        res.clearCookie(config.secureCookies ? '__Host-crm.sid' : 'crm.sid', { path: '/' });
        res.redirect(303, oidc ? oidc.logoutUrl() : '/signed-out');
    });
    return router;
}
//# sourceMappingURL=auth.js.map