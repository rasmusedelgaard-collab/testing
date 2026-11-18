import { timingSafeEqual } from 'node:crypto';
import type { Request, RequestHandler } from 'express';
import { newCsrfToken } from '../auth/establishSession';
import { ForbiddenError } from '../errors';

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

export function tokensMatch(a: string, b: string): boolean {
  const x = Buffer.from(a);
  const y = Buffer.from(b);
  return x.length === y.length && timingSafeEqual(x, y);
}

/** Returns the session's CSRF token, creating it lazily (so anonymous page views don't create sessions). */
export function ensureCsrfToken(req: Request): string {
  req.session.csrfToken ??= newCsrfToken();
  return req.session.csrfToken;
}

/** The one multipart route. Its token is verified inside the route after multipart parsing (see routes/settings.ts). */
export const MULTIPART_PATH = '/settings/general/logo';
export const isMultipart = (req: Request) => (req.get('content-type') ?? '').toLowerCase().startsWith('multipart/form-data');

export function csrfValid(req: Request, sent: unknown): boolean {
  const expected = req.session?.csrfToken;
  return typeof sent === 'string' && !!expected && tokensMatch(sent, expected);
}

/** Synchronizer-token check on every non-GET request. Needs the body parser and session first. */
export const csrfProtection: RequestHandler = (req, res, next) => {
  res.locals.csrfToken = () => ensureCsrfToken(req);
  res.locals.csrfField = () => `<input type="hidden" name="_csrf" value="${ensureCsrfToken(req)}">`;
  if (SAFE_METHODS.has(req.method)) return next();
  if (isMultipart(req) && req.method === 'POST' && req.path === MULTIPART_PATH) return next(); // checked after parsing
  const sent = (req.body as Record<string, unknown> | undefined)?._csrf ?? req.get('x-csrf-token');
  const expected = req.session?.csrfToken;
  if (typeof sent !== 'string' || !expected || !tokensMatch(sent, expected)) {
    return next(new ForbiddenError('Invalid or missing CSRF token'));
  }
  next();
};
