import type { NextFunction, Request, Response } from 'express';
import { describe, expect, it, vi } from 'vitest';
import { requireAuth, requireRole } from '../src/auth/requireRole';
import { ForbiddenError } from '../src/errors';
import type { SessionUser } from '../src/auth/types';

const user = (over: Partial<SessionUser> = {}): SessionUser => ({ userId: 5, oid: 'o', name: 'N', email: null, role: 'User', teamId: 2, authAt: 1_000_000, ...over });

function run(mw: (req: Request, res: Response, next: NextFunction) => unknown, req: Partial<Request>) {
  const res = { redirect: vi.fn(), locals: {} as Record<string, unknown> };
  const next = vi.fn();
  const full = { method: 'GET', originalUrl: '/accounts', ...req } as Request;
  return Promise.resolve(mw(full, res as unknown as Response, next)).then(() => ({ res, next, req: full }));
}

describe('requireAuth', () => {
  const mw = requireAuth({ absoluteHours: 10, now: () => 1_000_000 + 3_600_000 });

  it('redirects an unauthenticated GET to /signin with returnTo', async () => {
    const { res, next } = await run(mw, { session: {} as never });
    expect(res.redirect).toHaveBeenCalledWith(303, '/signin?returnTo=%2Faccounts');
    expect(next).not.toHaveBeenCalled();
  });

  it('redirects an unauthenticated POST to plain /signin', async () => {
    const { res } = await run(mw, { session: {} as never, method: 'POST' });
    expect(res.redirect).toHaveBeenCalledWith(303, '/signin');
  });

  it('drops an unsafe returnTo rather than echoing it', async () => {
    const { res } = await run(mw, { session: {} as never, originalUrl: '//evil.example.com' });
    expect(res.redirect).toHaveBeenCalledWith(303, '/signin?returnTo=%2F');
  });

  it('sets req.rls from the session user and continues', async () => {
    const { next, req } = await run(mw, { session: { user: user({ role: 'Manager', teamId: null }) } as never });
    expect(next).toHaveBeenCalled();
    expect(req.rls).toEqual({ userId: 5, role: 'Manager', teamId: null });
  });

  it('ends sessions older than the absolute timeout', async () => {
    const destroy = vi.fn((cb: () => void) => cb());
    const late = requireAuth({ absoluteHours: 10, now: () => 1_000_000 + 10 * 3_600_000 + 1 });
    const { res, next } = await run(late, { session: { user: user(), destroy } as never });
    expect(destroy).toHaveBeenCalled();
    expect(res.redirect).toHaveBeenCalledWith(303, '/signin?reason=expired');
    expect(next).not.toHaveBeenCalled();
  });
});

describe('requireRole', () => {
  it('lets Admin through an Admin route and a Manager route', async () => {
    for (const min of ['Admin', 'Manager'] as const) {
      const { next } = await run(requireRole(min), { rls: { userId: 1, role: 'Admin', teamId: null } });
      expect(next).toHaveBeenCalledWith();
    }
  });
  it.each(['User', 'Manager'] as const)('%s on an Admin route gets a ForbiddenError (403 page)', async (role) => {
    const { next } = await run(requireRole('Admin'), { rls: { userId: 1, role, teamId: null } });
    expect(next).toHaveBeenCalledWith(expect.any(ForbiddenError));
  });
  it('fails closed without an identity', async () => {
    const { next } = await run(requireRole('User'), {});
    expect(next).toHaveBeenCalledWith(expect.any(ForbiddenError));
  });
});
