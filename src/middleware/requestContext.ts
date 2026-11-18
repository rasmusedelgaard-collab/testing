import { randomUUID } from 'node:crypto';
import type { RequestHandler } from 'express';
import { logger } from '../logger';

/** Assigns a request id (shown as "Reference" on error pages) and logs one line per request. */
export const requestContext: RequestHandler = (req, res, next) => {
  req.id = randomUUID();
  res.setHeader('X-Request-Id', req.id);
  const start = process.hrtime.bigint();
  res.on('finish', () => {
    const ms = Number(process.hrtime.bigint() - start) / 1e6;
    logger.info({ reqId: req.id, userId: req.session?.user?.userId, ip: req.ip, method: req.method, route: req.path, status: res.statusCode, ms: Math.round(ms) }, 'request');
  });
  next();
};
