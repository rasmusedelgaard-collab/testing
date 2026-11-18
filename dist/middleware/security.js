"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.securityHeaders = securityHeaders;
const helmet_1 = __importDefault(require("helmet"));
/** Strict CSP (overview 10): no inline script or style anywhere. */
function securityHeaders(config) {
    return (0, helmet_1.default)({
        contentSecurityPolicy: {
            useDefaults: false,
            directives: {
                defaultSrc: ["'self'"],
                scriptSrc: ["'self'"],
                styleSrc: ["'self'"],
                imgSrc: ["'self'", 'data:'],
                objectSrc: ["'none'"],
                frameAncestors: ["'none'"],
                baseUri: ["'self'"],
                formAction: ["'self'", 'https://login.microsoftonline.com'],
                ...(config.secureCookies ? { upgradeInsecureRequests: [] } : {}),
            },
        },
        referrerPolicy: { policy: 'same-origin' },
        strictTransportSecurity: config.secureCookies ? { maxAge: 31536000, includeSubDomains: true } : false,
        crossOriginEmbedderPolicy: false,
    });
}
//# sourceMappingURL=security.js.map