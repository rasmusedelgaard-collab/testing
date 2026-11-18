"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
const app_1 = require("./app");
const config_1 = require("./config");
const system_1 = require("./db/system");
const logger_1 = require("./logger");
let config;
try {
    config = (0, config_1.getConfig)();
}
catch (err) {
    console.error(err instanceof config_1.ConfigError ? err.message : err);
    process.exit(1);
}
logger_1.logger.level = config.logLevel;
if (config.demoMode)
    logger_1.logger.warn('DEMO MODE: fake users, in-memory sessions, no database. Never use outside local development.');
if (!config.demoMode && config.authMode === 'test-access-code') {
    logger_1.logger.warn('TEST ACCESS CODE MODE: shared-code sign-in instead of Entra. Test data only.');
}
const app = (0, app_1.createApp)({ config });
// PORT may be a Windows named pipe (iisnode): pass the string through untouched.
const listenTarget = config.port;
const server = app.listen(listenTarget, () => logger_1.logger.info({ port: config.port }, 'listening'));
function shutdown(signal) {
    logger_1.logger.info({ signal }, 'shutting down');
    server.close(() => {
        void (0, system_1.closeDatabase)().finally(() => process.exit(0));
    });
    setTimeout(() => process.exit(1), 10_000).unref();
}
process.on('SIGTERM', () => shutdown('SIGTERM'));
process.on('SIGINT', () => shutdown('SIGINT'));
//# sourceMappingURL=server.js.map