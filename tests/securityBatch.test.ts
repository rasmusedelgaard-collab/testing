import type { NextFunction, Request, Response } from 'express';
import request from 'supertest';
import { describe, expect, it, vi } from 'vitest';
import type { IdClaims, OidcClient } from '../src/auth/msal';
import { requireAuth } from '../src/auth/requireRole';
import type { SessionUser } from '../src/auth/types';
import { cachedProbe, rateLimit } from '../src/middleware/rateLimit';
import { USER_DEACTIVATED, type UserRepo } from '../src/db/userRepo';
import { demoConfig, entraConfig, GUID, makeApp } from './helpers';

const user = (over: Partial<SessionUser> = {}): SessionUser => ({ userId: 5, oid: 'o', name: 'N', email: null, role: 'User', teamId: 2, authAt: 1_000, ...over });
const run = async (mw: ReturnType<typeof requireAuth>, session: unknown) => {
  const res = { redirect: vi.fn(), locals: {} as Record<string, unknown> };
  const next = vi.fn();
  await mw({ method: 'GET', originalUrl: '/', session } as unknown as Request, res as unknown as Response, next as NextFunction);
  return { res, next };
};

describe('requireAuth: IsActive re-check (L8)', () => {
  it('checks the database when never checked, then at most every 5 minutes', async () => {
    let now = 10_000;
    const isActive = vi.fn(async () => true);
    const mw = requireAuth({ absoluteHours: 10, now: () => now, isActive, recheckMs: 300_000 });
    const u = user({ authAt: 10_000 });
    await run(mw, { user: u });
    expect(isActive).toHaveBeenCalledTimes(1);
    now += 299_000;
    await run(mw, { user: u });
    expect(isActive).toHaveBeenCalledTimes(1);
    now += 2_000;
    await run(mw, { user: u });
    expect(isActive).toHaveBeenCalledTimes(2);
  });
  it('ends the session and redirects when the user was deactivated', async () => {
    const destroy = vi.fn((cb: () => void) => cb());
    const mw = requireAuth({ absoluteHours: 10, now: () => 2_000, isActive: async () => false });
    const { res, next } = await run(mw, { user: user(), destroy });
    expect(destroy).toHaveBeenCalled();
    expect(res.redirect).toHaveBeenCalledWith(303, '/signin?reason=disabled');
    expect(next).not.toHaveBeenCalled();
  });
});

describe('Entra login of a deactivated user (L8)', () => {
  it('shows no-access and creates no session user', async () => {
    let nonce = '';
    const oidc: OidcClient = {
      authUrl: async (p) => { nonce = p.nonce; return `https://login.microsoftonline.com/x?state=${p.state}`; },
      redeem: async () => ({ oid: '99999999-9999-4999-8999-999999999999', tid: GUID, nonce, roles: ['CRM.User'] }) as IdClaims,
      logoutUrl: () => '',
    };
    const users: UserRepo = { upsertOnLogin: async () => { throw Object.assign(new Error('User is deactivated'), { number: USER_DEACTIVATED }); } };
    const { app } = makeApp({ config: entraConfig(), oidc, userRepo: users });
    const agent = request.agent(app);
    const start = await agent.get('/auth/login');
    const state = new URL(start.headers.location as string).searchParams.get('state');
    const res = await agent.get(`/auth/redirect?code=c&state=${state}`);
    expect(res.headers.location).toBe('/no-access');
    expect((await agent.get('/')).status).toBe(303);
  });
});

describe('response hardening (L4, L5)', () => {
  it('authenticated pages are Cache-Control: no-store', async () => {
    const { app } = makeApp({ config: demoConfig() });
    const agent = request.agent(app);
    await agent.get('/__test/login?role=User');
    expect((await agent.get('/')).headers['cache-control']).toBe('no-store');
  });
  it('/readyz is cached for 10 s', async () => {
    const ping = vi.fn(async () => true);
    const { app } = makeApp({ config: demoConfig(), ping });
    await request(app).get('/readyz');
    await request(app).get('/readyz');
    expect(ping).toHaveBeenCalledTimes(1);
  });
  it('cachedProbe expires and shares an in-flight call', async () => {
    let now = 0;
    const fn = vi.fn(async () => true);
    const p = cachedProbe(fn, 10_000, () => now);
    await Promise.all([p(), p()]);
    expect(fn).toHaveBeenCalledTimes(1);
    now = 10_001;
    await p();
    expect(fn).toHaveBeenCalledTimes(2);
  });
  it('rateLimit returns 429 with Retry-After once the window budget is spent', async () => {
    let now = 0;
    const mw = rateLimit({ max: 2, windowMs: 1000, now: () => now });
    const out: number[] = [];
    for (let i = 0; i < 4; i++) {
      const res = { status: (c: number) => { out.push(c); return { type: () => ({ send: () => undefined }) }; }, set: () => undefined };
      mw({ ip: '1.1.1.1', socket: {} } as unknown as Request, res as unknown as Response, () => out.push(200));
    }
    expect(out).toEqual([200, 200, 429, 429]);
    now = 1001;
    mw({ ip: '1.1.1.1', socket: {} } as unknown as Request, {} as Response, () => out.push(201));
    expect(out.at(-1)).toBe(201);
  });
  it('/auth/* is rate limited per client', async () => {
    const { app } = makeApp({ config: demoConfig() });
    let last = 0;
    for (let i = 0; i < 125; i++) last = (await request(app).get('/auth/redirect')).status;
    expect(last).toBe(429);
  });
});
