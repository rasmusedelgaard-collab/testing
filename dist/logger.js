"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.logger = void 0;
const pino_1 = __importDefault(require("pino"));
/**
 * Never log tokens, cookies, form bodies, emails or names. Log oid/userId and request ids only.
 * The level is applied by server.ts after the configuration has been validated.
 */
exports.logger = (0, pino_1.default)({
    level: 'info',
    redact: ['req.headers.cookie', 'req.headers.authorization', '*.password', '*.token', '*.secret'],
});
//# sourceMappingURL=logger.js.map