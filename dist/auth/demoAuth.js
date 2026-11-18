"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.isRole = isRole;
exports.demoUser = demoUser;
exports.createDemoAuthRouter = createDemoAuthRouter;
const express_1 = require("express");
const establishSession_1 = require("./establishSession");
const returnTo_1 = require("./returnTo");
/**
 * No-DB demo/test sign-in for local UI work and e2e (NODE_ENV=test or DEMO_MODE=true). Config refuses
 * to start with DEMO_MODE in production or on App Service, so this router is never mounted there.
 */
const DEMO_USERS = {
    Admin: { userId: 1, oid: '00000000-0000-4000-8000-0000000d0001', name: 'Demo Admin', email: 'admin@demo.invalid', role: 'Admin', teamId: 1, authAt: 0 },
    Manager: { userId: 2, oid: '00000000-0000-4000-8000-0000000d0002', name: 'Demo Manager', email: 'manager@demo.invalid', role: 'Manager', teamId: 1, authAt: 0 },
    User: { userId: 3, oid: '00000000-0000-4000-8000-0000000d0003', name: 'Demo User', email: 'user@demo.invalid', role: 'User', teamId: 1, authAt: 0 },
};
function isRole(v) {
    return v === 'Admin' || v === 'Manager' || v === 'User';
}
function demoUser(role, now = Date.now()) {
    return { ...DEMO_USERS[role], authAt: now };
}
function createDemoAuthRouter(opts) {
    const router = (0, express_1.Router)();
    router.post('/demo/login', async (req, res) => {
        const role = req.body?.role;
        if (!isRole(role))
            return res.status(400).render('pages/error', { title: 'Bad request', heading: 'Choose a role', message: 'Pick Admin, Manager or User.', reference: req.id, status: 400 });
        await (0, establishSession_1.establishSession)(req, demoUser(role));
        res.redirect(303, (0, returnTo_1.safeReturnTo)(req.body?.returnTo));
    });
    if (opts.allowGetLogin) {
        // Test automation convenience (NODE_ENV=test only): GET /__test/login?role=Admin
        router.get('/__test/login', async (req, res) => {
            const role = req.query.role ?? 'User';
            if (!isRole(role))
                return res.status(400).send('bad role');
            await (0, establishSession_1.establishSession)(req, demoUser(role));
            res.redirect(303, (0, returnTo_1.safeReturnTo)(req.query.returnTo));
        });
    }
    return router;
}
//# sourceMappingURL=demoAuth.js.map