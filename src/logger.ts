import pino from 'pino';

/**
 * Never log tokens, cookies, form bodies, emails or names. Log oid/userId and request ids only.
 * The level is applied by server.ts after the configuration has been validated.
 */
export const logger = pino({
  level: 'info',
  redact: ['req.headers.cookie', 'req.headers.authorization', '*.password', '*.token', '*.secret'],
});
