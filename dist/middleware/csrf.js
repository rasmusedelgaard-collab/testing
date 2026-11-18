"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.csrfProtection = exports.isMultipart = exports.MULTIPART_PATH = void 0;
exports.tokensMatch = tokensMatch;
exports.ensureCsrfToken = ensureCsrfToken;
exports.csrfValid = csrfValid;
const node_crypto_1 = require("node:crypto");
const establishSession_1 = require("../auth/establishSession");
const errors_1 = require("../errors");
const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);
function tokensMatch(a, b) {
    const x = Buffer.from(a);
    const y = Buffer.from(b);
    return x.length === y.length && (0, node_crypto_1.timingSafeEqual)(x, y);
}
/** Returns the session's CSRF token, creating it lazily (so anonymous page views don't create sessions). */
function ensureCsrfToken(req) {
    req.session.csrfToken ??= (0, establishSession_1.newCsrfToken)();
    return req.session.csrfToken;
}
/** The one multipart route. Its token is verified inside the route after multipart parsing (see routes/settings.ts). */
exports.MULTIPART_PATH = '/settings/general/logo';
const isMultipart = (req) => (req.get('content-type') ?? '').toLowerCase().startsWith('multipart/form-data');
exports.isMultipart = isMultipart;
function csrfValid(req, sent) {
    const expected = req.session?.csrfToken;
    return typeof sent === 'string' && !!expected && tokensMatch(sent, expected);
}
/** Synchronizer-token check on every non-GET request. Needs the body parser and session first. */
const csrfProtection = (req, res, next) => {
    res.locals.csrfToken = () => ensureCsrfToken(req);
    res.locals.csrfField = () => `<input type="hidden" name="_csrf" value="${ensureCsrfToken(req)}">`;
    if (SAFE_METHODS.has(req.method))
        return next();
    if ((0, exports.isMultipart)(req) && req.method === 'POST' && req.path === exports.MULTIPART_PATH)
        return next(); // checked after parsing
    const sent = req.body?._csrf ?? req.get('x-csrf-token');
    const expected = req.session?.csrfToken;
    if (typeof sent !== 'string' || !expected || !tokensMatch(sent, expected)) {
        return next(new errors_1.ForbiddenError('Invalid or missing CSRF token'));
    }
    next();
};
exports.csrfProtection = csrfProtection;
//# sourceMappingURL=csrf.js.map