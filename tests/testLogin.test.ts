import request from 'supertest';
import { describe, expect, it, vi } from 'vitest';
import { AttemptLimiter } from '../src/auth/rateLimiter';
import { codeMatches, TEST_USER_OIDS, TEST_USERS } from '../src/auth/testLogin';
import type { TestUserRepo, TestUserRow } from '../src/db/testUserRepo';
import { allSessions, getCsrf, makeApp, testAccessConfig } from './helpers';

const CODE = 'correct-horse-battery-staple';

const ROWS: TestUserRow[] = [
  { userId: 101, oid: TEST_USER_OIDS[0]!, name: 'Test Admin', email: null, role: 'Admin', teamId: null, teamName: null },
  { userId: 102, oid: TEST_USER_OIDS[1]!, name: 'Test Manager A', email: 'm@test.invalid', role: 'Manager', teamId: 7, teamName: 'Test Team A' },
  { userId: 103, oid: TEST_USER_OIDS[2]!, name: 'Test User A1', email: null, role: 'User', teamId: 7, teamName: 'Test Team A' },
];

function fakeRepo(): TestUserRepo & { find: ReturnType<typeof vi.fn> } {
  return {
    list: vi.fn(async (oids: readonly string[]) => ROWS.filter((r) => oids.includes(r.oid))),
    find: vi.fn(async (oid: string, allowed: readonly string[]) => (allowed.includes(oid) ? (ROWS.find((r) => r.oid === oid) ?? null) : null)),
  };
}

function setup() {
  const repo = fakeRepo();
  const { app, store } = makeApp({ config: testAccessConfig(), testUserRepo: repo });
  return { app, store, repo };
}

const submitCode = async (agent: ReturnType<typeof request.agent>, code: string) => {
  const csrf = await getCsrf(agent, '/auth/test-login');
  return agent.post('/auth/test-login').type('form').send({ _csrf: csrf, code });
};

describe('test-access-code login', () => {
  it('shows a persistent TEST ENVIRONMENT banner on every page in this mode', async () => {
    const { app } = setup();
    for (const path of ['/signin', '/auth/test-login', '/signed-out']) {
      expect((await request(app).get(path)).text).toContain('TEST ENVIRONMENT — test data only');
    }
  });

  it('does not show the banner in entra mode', async () => {
    const { app } = makeApp({ config: testAccessConfig({ AUTH_MODE: 'entra', ENTRA_TENANT_ID: '11111111-2222-4333-8444-555555555555', ENTRA_CLIENT_ID: '11111111-2222-4333-8444-555555555555', ENTRA_CLIENT_SECRET: 's', REDIRECT_URI: 'http://localhost:3000/auth/redirect' }), oidc: { authUrl: async () => '', redeem: async () => { throw new Error('x'); }, logoutUrl: () => '' } });
    expect((await request(app).get('/signin')).text).not.toContain('TEST ENVIRONMENT');
  });

  it('rejects a wrong code and shows no user list', async () => {
    const { app, repo } = setup();
    const res = await submitCode(request.agent(app), 'wrong-code');
    expect(res.status).toBe(401);
    expect(res.text).toContain('not correct');
    expect(res.text).not.toContain('Test Admin');
    expect(repo.list).not.toHaveBeenCalled();
  });

  it('accepts the right code and lists only seeded users', async () => {
    const { app, repo } = setup();
    const res = await submitCode(request.agent(app), CODE);
    expect(res.status).toBe(200);
    expect(res.text).toContain('Test Manager A');
    expect(repo.list).toHaveBeenCalledWith(TEST_USER_OIDS);
    expect(TEST_USERS.map((u) => u.label)).toHaveLength(7);
  });

  it('signs in with session values taken from the DB row (not from the form)', async () => {
    const { app, store } = setup();
    const agent = request.agent(app);
    await submitCode(agent, CODE);
    const csrf = await getCsrf(agent, '/auth/test-login');
    const res = await agent.post('/auth/test-login/user').type('form').send({ _csrf: csrf, oid: ROWS[1]!.oid, role: 'Admin', userId: '1', teamId: '999' });
    expect(res.status).toBe(303);
    const [data] = Object.values(await allSessions(store));
    expect(data?.user).toMatchObject({ userId: 102, role: 'Manager', teamId: 7, oid: ROWS[1]!.oid, name: 'Test Manager A' });
    expect(data?.testAccess).toBeUndefined(); // session regenerated
    const home = await agent.get('/');
    expect(home.status).toBe(200);
    expect(home.text).toContain('TEST ENVIRONMENT');
    expect(home.text).not.toContain('/settings'); // Manager is not Admin
  });

  it('only seeded users can be selected', async () => {
    const { app, repo } = setup();
    const agent = request.agent(app);
    await submitCode(agent, CODE);
    const csrf = await getCsrf(agent, '/auth/test-login');
    const res = await agent.post('/auth/test-login/user').type('form').send({ _csrf: csrf, oid: '12345678-1234-4234-8234-123456789012' });
    expect(res.status).toBe(403);
    expect(repo.find).toHaveBeenCalledWith('12345678-1234-4234-8234-123456789012', TEST_USER_OIDS);
    expect((await agent.get('/')).status).toBe(303); // still signed out
  });

  it('refuses the user step without having passed the code step', async () => {
    const { app } = setup();
    const agent = request.agent(app);
    const csrf = await getCsrf(agent, '/auth/test-login');
    const res = await agent.post('/auth/test-login/user').type('form').send({ _csrf: csrf, oid: ROWS[0]!.oid });
    expect(res.status).toBe(303);
    expect(res.headers.location).toBe('/auth/test-login');
    expect((await agent.get('/')).status).toBe(303);
  });

  it('requires a CSRF token on both steps', async () => {
    const { app } = setup();
    expect((await request(app).post('/auth/test-login').type('form').send({ code: CODE })).status).toBe(403);
  });

  it('locks out after 5 wrong attempts, even for the correct code', async () => {
    const { app } = setup();
    const agent = request.agent(app);
    const csrf = await getCsrf(agent, '/auth/test-login');
    const post = (code: string) => agent.post('/auth/test-login').type('form').send({ _csrf: csrf, code });
    for (let i = 0; i < 4; i++) expect((await post('nope')).status).toBe(401);
    const fifth = await post('nope');
    expect(fifth.status).toBe(429);
    expect(fifth.text).toContain('Too many incorrect attempts');
    const correct = await post(CODE);
    expect(correct.status).toBe(429);
    expect(correct.text).not.toContain('Test Admin');
  });

  it('/auth/redirect and the demo routes are not mounted in this mode', async () => {
    const { app } = setup();
    expect((await request(app).get('/auth/redirect')).status).toBe(303); // auth gate, no handler
    expect((await request(app).get('/__test/login?role=Admin')).status).toBe(303);
  });
});

describe('global failed-attempt cap', () => {
  it('locks sign-in for everyone after 50 failures across different clients, even with the right code', async () => {
    const repo = fakeRepo();
    const { app } = makeApp({ config: testAccessConfig({ TRUST_PROXY: '1' }), testUserRepo: repo });
    const agent = request.agent(app);
    const csrf = await getCsrf(agent, '/auth/test-login');
    for (let i = 0; i < 50; i++) await agent.post('/auth/test-login').set('X-Forwarded-For', `10.1.${Math.floor(i / 250)}.${(i % 250) + 1}`).type('form').send({ _csrf: csrf, code: `wrong-${i}` });
    const res = await agent.post('/auth/test-login').set('X-Forwarded-For', '203.0.113.9').type('form').send({ _csrf: csrf, code: CODE });
    expect(res.status).toBe(429);
    expect(repo.list).not.toHaveBeenCalled();
  });
});

describe('codeMatches', () => {
  it('compares in constant time on equal-length digests', () => {
    expect(codeMatches(CODE, CODE)).toBe(true);
    expect(codeMatches('', CODE)).toBe(false);
    expect(codeMatches(CODE + 'x', CODE)).toBe(false);
    expect(codeMatches('a'.repeat(5000), CODE)).toBe(false);
  });
});

describe('AttemptLimiter', () => {
  it('locks after 5 failures and unlocks when the window has passed', () => {
    let now = 0;
    const l = new AttemptLimiter(5, 15 * 60_000, () => now);
    for (let i = 0; i < 4; i++) l.recordFailure('ip');
    expect(l.isLocked('ip')).toBe(false);
    l.recordFailure('ip');
    expect(l.isLocked('ip')).toBe(true);
    expect(l.isLocked('other')).toBe(false);
    now = 15 * 60_000;
    expect(l.isLocked('ip')).toBe(false);
  });
  it('reset clears the counter', () => {
    const l = new AttemptLimiter();
    for (let i = 0; i < 5; i++) l.recordFailure('ip');
    l.reset('ip');
    expect(l.isLocked('ip')).toBe(false);
  });
});
