import { createHash, timingSafeEqual } from 'node:crypto';
import { Router, type Request, type Response } from 'express';
import { ForbiddenError } from '../errors';
import type { TestUserRepo } from '../db/testUserRepo';
import { logger } from '../logger';
import { establishSession } from './establishSession';
import { AttemptLimiter } from './rateLimiter';
import { safeReturnTo } from './returnTo';

/**
 * Interim hosted test site only (AUTH_MODE=test-access-code, no Entra). A shared access code unlocks a
 * picker of the SEEDED test users from crm.Users. The session is then built from the DB row exactly like an
 * Entra login would, so withRlsContext and RLS behave identically downstream.
 */

/** Fixed object IDs created by db/seed/test-users.sql. Only these can ever be selected. */
export const TEST_USERS = [
  { oid: '00000000-0000-4000-8000-00000000a001', label: 'Admin' },
  { oid: '00000000-0000-4000-8000-00000000a002', label: 'Manager, Team A' },
  { oid: '00000000-0000-4000-8000-00000000a003', label: 'User 1, Team A' },
  { oid: '00000000-0000-4000-8000-00000000a004', label: 'User 2, Team A' },
  { oid: '00000000-0000-4000-8000-00000000b001', label: 'Manager, Team B' },
  { oid: '00000000-0000-4000-8000-00000000b002', label: 'User, Team B' },
  { oid: '00000000-0000-4000-8000-00000000c001', label: 'User, no team' },
] as const;
export const TEST_USER_OIDS: readonly string[] = TEST_USERS.map((u) => u.oid);

const CODE_VALID_FOR_MS = 10 * 60 * 1000;

/** Constant-time compare that does not leak the length of the secret (both sides hashed first). */
export function codeMatches(given: string, expected: string): boolean {
  const a = createHash('sha256').update(given).digest();
  const b = createHash('sha256').update(expected).digest();
  return timingSafeEqual(a, b);
}

export interface TestLoginDeps {
  accessCode: string;
  repo: TestUserRepo;
  limiter?: AttemptLimiter;
  /** Defence in depth: failed attempts across ALL clients. */
  globalLimiter?: AttemptLimiter;
  now?: () => number;
}

export function createTestLoginRouter(deps: TestLoginDeps): Router {
  const limiter = deps.limiter ?? new AttemptLimiter();
  const globalLimiter = deps.globalLimiter ?? new AttemptLimiter(50);
  const GLOBAL = 'all';
  // Never one shared 'unknown' bucket: without an address, key on the session so one anonymous client cannot lock everyone out.
  const keyOf = (req: Request) => req.ip ?? `session:${req.sessionID}`;
  const lockedFor = (req: Request) => limiter.isLocked(keyOf(req)) || globalLimiter.isLocked(GLOBAL);
  const now = deps.now ?? Date.now;
  const router = Router();

  router.get('/auth/test-login', (req, res) => {
    const verified = Boolean(req.session.testAccess && now() - req.session.testAccess.verifiedAt < CODE_VALID_FOR_MS);
    if (!verified) {
      return res.render('pages/test-login-code', { title: 'Test sign-in', error: null, returnTo: safeReturnTo(req.query.returnTo), locked: lockedFor(req) });
    }
    return renderPicker(req, res);
  });

  const renderPicker = async (req: Request, res: Response) => {
    const users = await deps.repo.list(TEST_USER_OIDS);
    res.render('pages/test-login-user', { title: 'Choose a test user', users, returnTo: safeReturnTo(req.body?.returnTo ?? req.query.returnTo) });
  };

  router.post('/auth/test-login', async (req, res) => {
    const ip = keyOf(req);
    const returnTo = safeReturnTo(req.body?.returnTo);
    if (lockedFor(req)) {
      logger.warn({ ip }, 'test access code attempt while locked out');
      return res.status(429).render('pages/test-login-code', { title: 'Test sign-in', error: null, returnTo, locked: true });
    }
    const code = typeof req.body?.code === 'string' ? req.body.code : '';
    if (!codeMatches(code, deps.accessCode)) {
      limiter.recordFailure(ip);
      globalLimiter.recordFailure(GLOBAL);
      logger.warn({ ip }, 'wrong test access code');
      if (globalLimiter.isLocked(GLOBAL)) logger.warn('test access code: global failed-attempt cap reached; sign-in locked for everyone');
      const locked = lockedFor(req);
      return res.status(locked ? 429 : 401).render('pages/test-login-code', {
        title: 'Test sign-in',
        error: locked ? null : 'That access code is not correct.',
        returnTo,
        locked,
      });
    }
    limiter.reset(ip); // the global counter is deliberately not reset by a success
    req.session.testAccess = { verifiedAt: now() };
    await new Promise<void>((resolve, reject) => req.session.save((e) => (e ? reject(e as Error) : resolve())));
    return renderPicker(req, res);
  });

  router.post('/auth/test-login/user', async (req, res) => {
    const access = req.session.testAccess;
    if (!access || now() - access.verifiedAt >= CODE_VALID_FOR_MS) {
      return res.redirect(303, '/auth/test-login');
    }
    const oid = typeof req.body?.oid === 'string' ? req.body.oid.toLowerCase() : '';
    const row = await deps.repo.find(oid, TEST_USER_OIDS);
    if (!row) throw new ForbiddenError('That test user is not available.');
    const returnTo = safeReturnTo(req.body?.returnTo);
    await establishSession(req, {
      userId: row.userId,
      oid: row.oid,
      name: row.name,
      email: row.email,
      role: row.role,
      teamId: row.teamId,
      authAt: now(),
    });
    logger.info({ oid: row.oid, role: row.role }, 'test login');
    res.redirect(303, returnTo);
  });

  return router;
}
