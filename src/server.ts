import { createApp } from './app';
import { ConfigError, getConfig, type AppConfig } from './config';
import { closeDatabase } from './db/system';
import { logger } from './logger';

let config: AppConfig;
try {
  config = getConfig();
} catch (err) {
  console.error(err instanceof ConfigError ? err.message : err);
  process.exit(1);
}
logger.level = config.logLevel;
if (config.demoMode) logger.warn('DEMO MODE: fake users, in-memory sessions, no database. Never use outside local development.');
if (!config.demoMode && config.authMode === 'test-access-code') {
  logger.warn('TEST ACCESS CODE MODE: shared-code sign-in instead of Entra. Test data only.');
}

const app = createApp({ config });
// PORT may be a Windows named pipe (iisnode): pass the string through untouched.
const listenTarget: string | number = config.port;
const server = app.listen(listenTarget, () => logger.info({ port: config.port }, 'listening'));

function shutdown(signal: string) {
  logger.info({ signal }, 'shutting down');
  server.close(() => {
    void closeDatabase().finally(() => process.exit(0));
  });
  setTimeout(() => process.exit(1), 10_000).unref();
}
process.on('SIGTERM', () => shutdown('SIGTERM'));
process.on('SIGINT', () => shutdown('SIGINT'));
