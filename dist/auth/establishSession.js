"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.newCsrfToken = newCsrfToken;
exports.establishSession = establishSession;
exports.destroySession = destroySession;
const node_crypto_1 = require("node:crypto");
function newCsrfToken() {
    return (0, node_crypto_1.randomBytes)(32).toString('base64url');
}
/**
 * Starts the authenticated session: regenerate (prevents fixation), store the identity and a fresh
 * CSRF token, and persist before the caller redirects.
 */
function establishSession(req, user) {
    return new Promise((resolve, reject) => {
        req.session.regenerate((err) => {
            if (err)
                return reject(err);
            req.session.user = { ...user, activeCheckedAt: user.authAt };
            req.session.csrfToken = newCsrfToken();
            req.session.save((saveErr) => (saveErr ? reject(saveErr) : resolve()));
        });
    });
}
function destroySession(req) {
    return new Promise((resolve) => {
        req.session.destroy(() => resolve());
    });
}
//# sourceMappingURL=establishSession.js.map