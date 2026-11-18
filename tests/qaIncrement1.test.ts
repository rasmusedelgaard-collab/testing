/**
 * QA tests for increment 1 (foundation): edge cases and adverse paths that complement the developer's tests.
 * Tests marked it.fails document an OPEN DEFECT: they assert the desired behaviour, pass while the defect exists
 * and turn red (it.fails reports "expected to fail") as soon as it is fixed, so remove the marker then.
 */
import request from 'supertest';
import { describe, expect, it, vi } from 'vitest';
import type { OidcClient } from '../src/auth/msal';
import { mapRole } from '../src/auth/roles';
import { TEST_USER_OIDS } from '../src/auth/testLogin';
import { loadConfig } from '../src/config';
import { mapDbError } from '../src/db/mapDbError';
import { isTransient } from '../src/db/transient';
import { DbUnavailableError, ForbiddenError } from '../src/errors';
import type { TestUserRepo, TestUserRow } from '../src/db/testUserRepo';
import type { UserRepo } from '../src/db/userRepo';
import { demoConfig, entraConfig, getCsrf, GUID, makeApp, testAccessConfig } from './helpers';

describe('role mapping edge cases (acceptance 3)', () => {
  it.each([
    [undefined], [null], ['CRM.Admin'], [{ 0: 'CRM.Admin' }], [[]], [['crm.admin']], [['CRM.Admin ']], [['Admin', 'Manager', 'User']], [[1, null, {}]],
  ])('%j gives no access', (input) => {
    expect(mapRole(input)).toBeNull();
  });
  it('Admin wins over Manager and User in any order, duplicates are harmless', () => {
    expect(mapRole(['CRM.User', 'CRM.Manager', 'CRM.Admin', 'CRM.Admin'])).toBe('Admin');
    expect(mapRole(['CRM.User', 'CRM.Manager'])).toBe('Manager');
    expect(mapRole(['x', 5, 'CRM.User'])).toBe('User');
  });
});

describe('Entra sign-in without a CRM role (acceptance 3)', () => {
  function setup(roles: string[] | undefined) {
    let last: { state: string; nonce: string } | undefined;
    const oidc: OidcClient = {
      authUrl: async (p) => { last = p; return 'https://login.microsoftonline.com/x'; },
      redeem: async () => ({ oid: '99999999-9999-4999-8999-999999999999', tid: GUID, nonce: last!.nonce, name: 'No Role', roles }),
      logoutUrl: () => 'https://login.microsoftonline.com/logout',
    };
    const users: UserRepo = { upsertOnLogin: vi.fn(async () => ({ userId: 1, teamId: null, teamStatus: 'none' as const, teamMatches: 0 })) };
    const { app } = makeApp({ config: entraConfig(), oidc, userRepo: users });
    return { app, users, state: () => last!.state };
  }
  it.each([[undefined], [[]], [['Some.Other.Role']]])('roles=%j: 403 no-access, no DB write, still not signed in', async (roles) => {
    const { app, users, state } = setup(roles as string[] | undefined);
    const agent = request.agent(app);
    await agent.get('/auth/login');
    const res = await agent.get(`/auth/redirect?code=c&state=${state()}`);
    expect(res.status).toBe(303);
    expect(res.headers.location).toBe('/no-access');
    expect((await agent.get('/no-access')).status).toBe(403);
    expect(users.upsertOnLogin).not.toHaveBeenCalled();
    const home = await agent.get('/');
    expect(home.status).toBe(303);
    expect(home.headers.location).toContain('/signin');
  });
  it('unauthenticated access redirects to /signin in entra mode for every app route', async () => {
    const { app } = setup(['CRM.User']);
    for (const p of ['/', '/accounts', '/contacts', '/opportunities', '/activities', '/analytics', '/settings/general', '/search?q=x']) {
      const res = await request(app).get(p);
      expect(res.status, p).toBe(303);
      expect(res.headers.location, p).toMatch(/^\/signin/);
    }
  });
});

describe('database error mapping (acceptance 5)', () => {
  const err = (number: number) => Object.assign(new Error('x'), { number });
  it('maps every documented transient number to a 503 error and unknown numbers to themselves', () => {
    for (const n of [40613, 40197, 40501, 40540, 49918, 49919, 49920, 4221, 10928, 10929]) {
      expect(mapDbError(err(n))).toBeInstanceOf(DbUnavailableError);
    }
    const unknown = err(8152); // string truncation: must not be disguised as anything else
    expect(mapDbError(unknown)).toBe(unknown);
  });
  it('recognises the error number when nested in originalError / cause', () => {
    expect(mapDbError({ originalError: { info: { number: 33504 } } })).toBeInstanceOf(ForbiddenError);
    expect(mapDbError(new Error('wrapped', { cause: err(33504) }))).toBeInstanceOf(ForbiddenError);
  });
  it('treats socket errors as transient and plain errors as not', () => {
    expect(isTransient(Object.assign(new Error('x'), { code: 'ETIMEOUT' }))).toBe(true);
    expect(isTransient(new Error('boom'))).toBe(false);
    expect(mapDbError(null)).toBeNull();
    expect(mapDbError('str')).toBe('str');
  });
});

describe('config boundaries (acceptance 6, 7, 8)', () => {
  const base = { NODE_ENV: 'development', AUTH_MODE: 'test-access-code', SESSION_SECRET: 'z'.repeat(32), DB_SERVER: 's', DB_NAME: 'd', DB_AUTH_MODE: 'sql-password', DB_USER: 'u', DB_PASSWORD: 'p' };
  it('TEST_ACCESS_CODE: 23 chars rejected, 24 varied chars accepted, repetitive rejected', () => {
    const ok = 'ab12cd34ef56gh78ij90kl12';
    expect(() => loadConfig({ ...base, TEST_ACCESS_CODE: ok.slice(0, 23) })).toThrow(/TEST_ACCESS_CODE/);
    expect(() => loadConfig({ ...base, TEST_ACCESS_CODE: 'a'.repeat(30) })).toThrow(/repetitive/);
    expect(loadConfig({ ...base, TEST_ACCESS_CODE: ok }).testAccessCode).toBe(ok);
    expect(() => loadConfig({ ...base })).toThrow(/TEST_ACCESS_CODE/);
  });
  it('SESSION_SECRET: 31 chars rejected, 32 accepted', () => {
    expect(() => loadConfig({ ...base, TEST_ACCESS_CODE: 'ab12cd34ef56gh78ij90kl12', SESSION_SECRET: 'z'.repeat(31) })).toThrow(/SESSION_SECRET/);
  });
  it('NODE_ENV=production with DEMO_MODE=true is refused, even with otherwise complete config', () => {
    expect(() => loadConfig({ ...base, NODE_ENV: 'production', AUTH_MODE: 'entra', DEMO_MODE: 'true', TEST_ACCESS_CODE: 'a'.repeat(16) })).toThrow(/DEMO_MODE/);
  });
  it('DEMO_MODE=true is refused on App Service in any NODE_ENV; DEMO_MODE=false never enables demo', () => {
    expect(() => loadConfig({ NODE_ENV: 'development', DEMO_MODE: 'true', WEBSITE_SITE_NAME: 'x' })).toThrow(/DEMO_MODE/);
    expect(() => loadConfig({ NODE_ENV: 'development', DEMO_MODE: 'false' })).toThrow(/SESSION_SECRET|REDIRECT_URI/);
  });
  it('DEMO_MODE must be exactly true/false (no "1", "TRUE", "yes")', () => {
    for (const v of ['1', 'TRUE', 'yes', '']) expect(() => loadConfig({ NODE_ENV: 'development', DEMO_MODE: v })).toThrow();
  });
  it('PORT: numeric string and named pipes are passed through verbatim as strings', () => {
    expect(demoConfig({ PORT: '8080' }).port).toBe('8080');
    const pipe = '\\\\.\\pipe\\6d1c2a3e-1111-4222-8333-444455556666';
    expect(demoConfig({ PORT: pipe }).port).toBe(pipe);
    expect(demoConfig({ PORT: '\\\\?\\pipe\\abc' }).port).toBe('\\\\?\\pipe\\abc');
    for (const bad of ['abc', '-1', '80 80', '/tmp/sock', '\\\\.\\pipe\\', '3000;rm']) expect(() => demoConfig({ PORT: bad }), bad).toThrow(/PORT/);
  });
  // QA-3 fixed: PORT range is checked.
  it('PORT above 65535 is rejected at config time (QA-3)', () => {
    expect(() => demoConfig({ PORT: '99999' })).toThrow(/PORT/);
  });
});

describe('/__test routes exist only under NODE_ENV=test (acceptance 7)', () => {
  const rows: TestUserRow[] = [{ userId: 1, oid: TEST_USER_OIDS[0]!, name: 'A', email: null, role: 'Admin', teamId: null, teamName: null }];
  const repo: TestUserRepo = { list: async () => rows, find: async () => rows[0]! };
  it.each([
    ['development demo', () => makeApp({ config: demoConfig({ NODE_ENV: 'development', DEMO_MODE: 'true' }) }).app],
    ['test-access-code', () => makeApp({ config: testAccessConfig(), testUserRepo: repo }).app],
    ['entra', () => makeApp({ config: entraConfig() }).app],
  ])('%s: /__test/login never signs anyone in', async (_n, mk) => {
    const agent = request.agent(mk());
    const res = await agent.get('/__test/login?role=Admin');
    expect([303, 404]).toContain(res.status);
    if (res.status === 303) expect(res.headers.location).toMatch(/^\/signin/);
    const home = await agent.get('/');
    expect(home.status).toBe(303);
  });
  it('is mounted under NODE_ENV=test and rejects an unknown role', async () => {
    const { app } = makeApp({ config: demoConfig() });
    expect((await request(app).get('/__test/login?role=Root')).status).toBe(400);
    expect((await request(app).get('/__test/login?role=Admin')).status).toBe(303);
  });
});

describe('test-access-code mode: banner, rate limit, error paths (acceptance 6)', () => {
  const rows: TestUserRow[] = TEST_USER_OIDS.map((oid, i) => ({ userId: i + 1, oid, name: `U${i}`, email: null, role: i === 0 ? 'Admin' : 'User', teamId: null, teamName: null }));
  const repo: TestUserRepo = { list: async (o) => rows.filter((r) => o.includes(r.oid)), find: async (o, a) => (a.includes(o) ? (rows.find((r) => r.oid === o) ?? null) : null) };
  const mk = () => makeApp({ config: testAccessConfig(), testUserRepo: repo }).app;

  it('banner is on the 403 (CSRF) and no-access pages as well', async () => {
    const app = mk();
    const csrf403 = await request(app).post('/auth/test-login').type('form').send({ code: 'x' });
    expect(csrf403.status).toBe(403);
    expect(csrf403.text).toContain('TEST ENVIRONMENT');
    const agent = request.agent(app);
    const csrf = await getCsrf(agent, '/auth/test-login');
    const noAccess = await agent.get('/no-access');
    expect(noAccess.status).toBe(403);
    expect(noAccess.text).toContain('TEST ENVIRONMENT');
    expect(csrf).toBeTruthy();
  });

  it('non-string / array code values are rejected, not crashed on', async () => {
    const app = mk();
    const agent = request.agent(app);
    const csrf = await getCsrf(agent, '/auth/test-login');
    const res = await agent.post('/auth/test-login').type('form').send(`_csrf=${csrf}&code[]=a&code[]=b`);
    expect(res.status).toBe(401);
  });

  it('a user oid that is only in a different letter case still resolves to a seeded user only', async () => {
    const app = mk();
    const agent = request.agent(app);
    const csrf = await getCsrf(agent, '/auth/test-login');
    await agent.post('/auth/test-login').type('form').send({ _csrf: csrf, code: 'correct-horse-battery-staple' });
    const csrf2 = await getCsrf(agent, '/auth/test-login');
    const bad = await agent.post('/auth/test-login/user').type('form').send({ _csrf: csrf2, oid: '00000000-0000-4000-8000-0000000d0001' });
    expect(bad.status).toBe(403);
  });

  // QA-1 fixed: trust proxy defaults to 0 outside https deployments, and a global failed-attempt cap exists.
  // X-Forwarded-For (e.g. IIS/iisnode on the interim host) a client can rotate the header per request and
  // never reach the 5-attempt lockout.
  it('rate limit cannot be evaded by rotating X-Forwarded-For (QA-1)', async () => {
    const app = mk();
    const agent = request.agent(app);
    const csrf = await getCsrf(agent, '/auth/test-login');
    let last = 0;
    for (let i = 0; i < 8; i++) {
      const r = await agent.post('/auth/test-login').set('X-Forwarded-For', `10.0.0.${i + 1}`).type('form').send({ _csrf: csrf, code: `wrong-${i}` });
      last = r.status;
    }
    expect(last).toBe(429);
  });
});
