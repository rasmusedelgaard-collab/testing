import pino from 'pino';
import { getConfig } from './config';

/** Never log tokens, cookies, form bodies, emails or names. Log oid/userId and request ids only. */
export const logger = pino({
  level: getConfig().logLevel,
  redact: ['req.headers.cookie', 'req.headers.authorization', '*.password', '*.token', '*.secret'],
});
