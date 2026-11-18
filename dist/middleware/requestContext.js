"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.requestContext = void 0;
const node_crypto_1 = require("node:crypto");
const logger_1 = require("../logger");
/** Assigns a request id (shown as "Reference" on error pages) and logs one line per request. */
const requestContext = (req, res, next) => {
    req.id = (0, node_crypto_1.randomUUID)();
    res.setHeader('X-Request-Id', req.id);
    const start = process.hrtime.bigint();
    res.on('finish', () => {
        const ms = Number(process.hrtime.bigint() - start) / 1e6;
        logger_1.logger.info({ reqId: req.id, userId: req.session?.user?.userId, ip: req.ip, method: req.method, route: req.path, status: res.statusCode, ms: Math.round(ms) }, 'request');
    });
    next();
};
exports.requestContext = requestContext;
//# sourceMappingURL=requestContext.js.map