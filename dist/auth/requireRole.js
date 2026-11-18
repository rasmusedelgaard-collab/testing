"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.requireAuth = requireAuth;
exports.requireRole = requireRole;
const errors_1 = require("../errors");
const establishSession_1 = require("./establishSession");
const returnTo_1 = require("./returnTo");
const roles_1 = require("./roles");
/**
 * Requires a signed-in user. Unauthenticated GETs go to /signin with returnTo; the absolute session
 * timeout is checked here. Sets req.rls, the only source of identity for withRlsContext.
 */
function requireAuth(opts) {
    const now = opts.now ?? Date.now;
    return async (req, res, next) => {
        const user = req.session?.user;
        if (!user) {
            const target = req.method === 'GET' ? `/signin?returnTo=${encodeURIComponent((0, returnTo_1.safeReturnTo)(req.originalUrl))}` : '/signin';
            return res.redirect(303, target);
        }
        if (now() - user.authAt > opts.absoluteHours * 3_600_000) {
            await (0, establishSession_1.destroySession)(req);
            return res.redirect(303, '/signin?reason=expired');
        }
        if (opts.isActive && (user.activeCheckedAt === undefined || now() - user.activeCheckedAt > (opts.recheckMs ?? 300_000))) {
            if (!(await opts.isActive(user.userId))) {
                await (0, establishSession_1.destroySession)(req);
                return res.redirect(303, '/signin?reason=disabled');
            }
            user.activeCheckedAt = now();
        }
        req.rls = { userId: user.userId, role: user.role, teamId: user.teamId };
        res.locals.currentUser = user;
        next();
    };
}
/** Minimum-role check (Admin > Manager > User). RLS remains the real row-level enforcement. */
function requireRole(minimum) {
    return (req, _res, next) => {
        const role = req.rls?.role;
        if (!role || !(0, roles_1.hasAtLeast)(role, minimum))
            return next(new errors_1.ForbiddenError());
        next();
    };
}
//# sourceMappingURL=requireRole.js.map