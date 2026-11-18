import type { Request, RequestHandler } from 'express';
import { ForbiddenError } from '../errors';
import { destroySession } from './establishSession';
import { safeReturnTo } from './returnTo';
import { hasAtLeast } from './roles';
import type { Role } from './types';

/**
 * Requires a signed-in user. Unauthenticated GETs go to /signin with returnTo; the absolute session
 * timeout is checked here. Sets req.rls, the only source of identity for withRlsContext.
 */
export function requireAuth(opts: { absoluteHours: number; now?: () => number }): RequestHandler {
  const now = opts.now ?? Date.now;
  return async (req, res, next) => {
    const user = req.session?.user;
    if (!user) {
      const target = req.method === 'GET' ? `/signin?returnTo=${encodeURIComponent(safeReturnTo(req.originalUrl))}` : '/signin';
      return res.redirect(303, target);
    }
    if (now() - user.authAt > opts.absoluteHours * 3_600_000) {
      await destroySession(req);
      return res.redirect(303, '/signin?reason=expired');
    }
    req.rls = { userId: user.userId, role: user.role, teamId: user.teamId };
    res.locals.currentUser = user;
    next();
  };
}

/** Minimum-role check (Admin > Manager > User). RLS remains the real row-level enforcement. */
export function requireRole(minimum: Role): RequestHandler {
  return (req: Request, _res, next) => {
    const role = req.rls?.role;
    if (!role || !hasAtLeast(role, minimum)) return next(new ForbiddenError());
    next();
  };
}
