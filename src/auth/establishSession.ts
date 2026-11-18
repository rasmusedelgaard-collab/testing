import { randomBytes } from 'node:crypto';
import type { Request } from 'express';
import type { SessionUser } from './types';

export function newCsrfToken(): string {
  return randomBytes(32).toString('base64url');
}

/**
 * Starts the authenticated session: regenerate (prevents fixation), store the identity and a fresh
 * CSRF token, and persist before the caller redirects.
 */
export function establishSession(req: Request, user: SessionUser): Promise<void> {
  return new Promise((resolve, reject) => {
    req.session.regenerate((err) => {
      if (err) return reject(err as Error);
      req.session.user = { ...user, activeCheckedAt: user.authAt };
      req.session.csrfToken = newCsrfToken();
      req.session.save((saveErr) => (saveErr ? reject(saveErr as Error) : resolve()));
    });
  });
}

export function destroySession(req: Request): Promise<void> {
  return new Promise((resolve) => {
    req.session.destroy(() => resolve());
  });
}
