import { describe, expect, it } from 'vitest';
import { loadConfig, loadDbConfig, loadTestAdmin } from '../src/config';
import { ConnectionStringError, parseConnectionString } from '../src/db/connectionString';
import { buildSqlConfig } from '../src/db/sqlConfig';
import { testAdminOid, TEST_OID_PREFIX } from '../src/testUsers';
import { TEST_ADMIN_UPSERT_SQL, testAdminParams } from '../scripts/testAdminSeed';
import { TEST_USER_OIDS } from '../src/auth/testLogin';
import { createTestLoginRouter } from '../src/auth/testLogin';
import request from 'supertest';
import { vi } from 'vitest';
import type { TestUserRepo, TestUserRow } from '../src/db/testUserRepo';
import { allSessions, getCsrf, makeApp, testAccessConfig } from './helpers';

const OK = 'Server=db.example.com;Database=crm1;User Id=appuser;Password=s3cr3t-pw;';

describe('parseConnectionString', () => {
  it('parses the usual shape', () => {
    expect(parseConnectionString(OK)).toEqual({ server: 'db.example.com', database: 'crm1', user: 'appuser', password: 's3cr3t-pw', trustServerCertificate: false });
  });
  it('accepts aliases, case, spacing, tcp: prefix, port, trailing semicolon and TrustServerCertificate', () => {
    const p = parseConnectionString(' data source = tcp:db.example.com,1433 ;Initial Catalog=crm1; UID=u ; PWD=p ;Encrypt=True;TrustServerCertificate=True;Persist Security Info=False;Connection Timeout=30');
    expect(p).toEqual({ server: 'db.example.com', port: 1433, database: 'crm1', user: 'u', password: 'p', trustServerCertificate: true });
  });
  it('supports quoted values so a password may contain ; = and quotes', () => {
    expect(parseConnectionString(`Server=h;Database=d;User Id=u;Password="a;b=c""d";`).password).toBe('a;b=c"d');
    expect(parseConnectionString(`Server=h;Database=d;User Id=u;Password='x;y'`).password).toBe('x;y');
    expect(parseConnectionString('Server=h;Database=d;User Id=u;Password=pa=ss').password).toBe('pa=ss');
  });
  it.each([
    ['', /missing/], ['Server=h;Database=d;User Id=u', /missing Password/], ['Server=h;Database=d;Password=p', /missing User Id/], ['garbage', /no "="/],
    ['Server=h;Database=d;User Id=u;Password=p;Foo=bar', /unknown key "Foo"/], ['Server=h;Database=d;User Id=u;Password=p;Server=h2', /duplicate key/],
    ['Server=h;Database=d;User Id=u;Password=p;Encrypt=False', /Encrypt=False/], ['Server=h;Database=d;User Id=u;Password=p;Integrated Security=True', /not supported/],
    ['Server=h;Database=d;User Id=u;Password=p;Authentication=Active Directory Default', /not supported/], ['Server=h\\inst;Database=d;User Id=u;Password=p', /host or host,port/],
    ['Server=h,99999;Database=d;User Id=u;Password=p', /port/], ['Server=h;Database=d;User Id=u;Password="unterminated', /unterminated/], ['Server=h;Database=d;User Id=u;Password="a"b', /text after/],
    ['Server=h;Database=d;User Id=u;Password=p;TrustServerCertificate=maybe', /TrustServerCertificate/], [`Server=h;Database=d;User Id=u;Password=${'x'.repeat(300)}`, /invalid "Password"/],
  ])('refuses %j', (input, why) => {
    expect(() => parseConnectionString(input)).toThrow(ConnectionStringError);
    expect(() => parseConnectionString(input)).toThrow(why);
  });
  it('error messages never contain the password or other values', () => {
    for (const bad of ['Server=h;Database=d;User Id=u;Password=TOPSECRET;Foo=bar', 'Server=h;Database=d;User Id=u;Password=TOPSECRET;Encrypt=False', 'Server=h,99999;Database=d;User Id=u;Password=TOPSECRET']) {
      try { parseConnectionString(bad); } catch (e) { expect((e as Error).message).not.toContain('TOPSECRET'); }
    }
  });
});

describe('config: DB_CONNECTION_STRING', () => {
  const base = { NODE_ENV: 'production', AUTH_MODE: 'test-access-code', REDIRECT_URI: 'https://t.example.com/auth/redirect', TEST_ACCESS_CODE: 'a-long-shared-code-1234-xyz', SESSION_SECRET: 'x'.repeat(32), DB_AUTH_MODE: 'sql-password' };
  it('works instead of the split variables', () => {
    const c = loadConfig({ ...base, DB_CONNECTION_STRING: OK });
    expect(c.db).toMatchObject({ server: 'db.example.com', database: 'crm1', user: 'appuser', password: 's3cr3t-pw', authMode: 'sql-password' });
    expect(loadDbConfig({ DB_AUTH_MODE: 'sql-password', DB_CONNECTION_STRING: OK })).toMatchObject({ database: 'crm1' });
  });
  it('the split variables still work', () => {
    expect(loadConfig({ ...base, DB_SERVER: 's', DB_NAME: 'd', DB_USER: 'u', DB_PASSWORD: 'p' }).db?.server).toBe('s');
  });
  it('both at once is an error', () => {
    for (const extra of [{ DB_SERVER: 's' }, { DB_NAME: 'd' }, { DB_USER: 'u' }, { DB_PASSWORD: 'p' }]) {
      expect(() => loadConfig({ ...base, DB_CONNECTION_STRING: OK, ...extra })).toThrow(/must not be set together with DB_CONNECTION_STRING/);
    }
  });
  it('is only allowed with DB_AUTH_MODE=sql-password', () => {
    expect(() => loadConfig({ ...base, DB_AUTH_MODE: 'managed-identity', DB_CONNECTION_STRING: OK })).toThrow(/only allowed when DB_AUTH_MODE=sql-password/);
  });
  it('is refused when live, like sql-password', () => {
    expect(() => loadConfig({ NODE_ENV: 'production', REDIRECT_URI: 'https://x.example.com/auth/redirect', ENTRA_TENANT_ID: '11111111-2222-4333-8444-555555555555', ENTRA_CLIENT_ID: '11111111-2222-4333-8444-555555555555', ENTRA_CLIENT_SECRET: 's', SESSION_SECRET: 'x'.repeat(32), DB_AUTH_MODE: 'sql-password', DB_CONNECTION_STRING: OK, LIVE: 'true' })).toThrow(/sql-password is refused/);
  });
  it('a bad string is reported without leaking the password', () => {
    let msg = '';
    try { loadConfig({ ...base, DB_CONNECTION_STRING: 'Server=h;Database=d;User Id=u;Password=LEAKME;Bogus=1' }); } catch (e) { msg = (e as Error).message; }
    expect(msg).toContain('DB_CONNECTION_STRING: invalid: unknown key "Bogus"');
    expect(msg).not.toContain('LEAKME');
  });
  it('port and certificate trust reach the driver config', () => {
    const c = loadDbConfig({ DB_AUTH_MODE: 'sql-password', DB_CONNECTION_STRING: 'Server=tcp:h,1444;Database=d;User Id=u;Password=p;TrustServerCertificate=True' });
    const sqlCfg = buildSqlConfig(c, { max: 2 });
    expect(sqlCfg).toMatchObject({ server: 'h', port: 1444, user: 'u', password: 'p', options: { encrypt: true, trustServerCertificate: true } });
  });
  it('the config never logs or exposes the raw string', () => {
    expect(JSON.stringify(loadConfig({ ...base, DB_CONNECTION_STRING: OK }).db)).not.toContain('Server=');
  });
});

describe('named test Admin (TEST_ADMIN_NAME / TEST_ADMIN_EMAIL)', () => {
  const env = { NODE_ENV: 'production', AUTH_MODE: 'test-access-code', REDIRECT_URI: 'https://t.example.com/auth/redirect', TEST_ACCESS_CODE: 'a-long-shared-code-1234-xyz', SESSION_SECRET: 'x'.repeat(32), DB_AUTH_MODE: 'sql-password', DB_SERVER: 's', DB_NAME: 'd', DB_USER: 'u', DB_PASSWORD: 'p' };
  it('is optional, needs both variables, and gets a deterministic oid in the reserved test range', () => {
    expect(loadConfig(env).testAdmin).toBeNull();
    const a = loadConfig({ ...env, TEST_ADMIN_NAME: 'Test Person', TEST_ADMIN_EMAIL: 'Person@Example.com' }).testAdmin!;
    expect(a).toMatchObject({ name: 'Test Person', email: 'person@example.com' });
    expect(a.oid).toBe(testAdminOid('person@example.com'));
    expect(a.oid.startsWith(TEST_OID_PREFIX)).toBe(true);
    expect(a.oid).toMatch(/^00000000-0000-4000-8000-[0-9a-f]{12}$/);
    expect(testAdminOid('other@example.com')).not.toBe(a.oid);
    expect(TEST_USER_OIDS).not.toContain(a.oid);
    expect(() => loadConfig({ ...env, TEST_ADMIN_NAME: 'x' })).toThrow(/set together/);
    expect(() => loadConfig({ ...env, TEST_ADMIN_EMAIL: 'a@b.co' })).toThrow(/set together/);
    expect(() => loadConfig({ ...env, TEST_ADMIN_NAME: 'x', TEST_ADMIN_EMAIL: 'not-an-email' })).toThrow(/TEST_ADMIN_EMAIL/);
    expect(() => loadConfig({ ...env, TEST_ADMIN_NAME: 'x'.repeat(201), TEST_ADMIN_EMAIL: 'a@b.co' })).toThrow(/TEST_ADMIN_NAME/);
  });
  it('is refused outside test-access-code mode', () => {
    expect(() => loadConfig({ NODE_ENV: 'development', DEMO_MODE: 'true', TEST_ADMIN_NAME: 'x', TEST_ADMIN_EMAIL: 'a@b.co' })).toThrow(/only allowed with AUTH_MODE=test-access-code/);
  });
  it('loadTestAdmin works for the seed script on its own', () => {
    expect(loadTestAdmin({})).toBeNull();
    expect(loadTestAdmin({ TEST_ADMIN_NAME: ' N ', TEST_ADMIN_EMAIL: ' A@B.CO ' })).toMatchObject({ name: 'N', email: 'a@b.co' });
  });
  it('the seed upsert uses only parameters and creates an active team-less Admin', () => {
    expect(TEST_ADMIN_UPSERT_SQL).toContain("Role = 'Admin'");
    expect(TEST_ADMIN_UPSERT_SQL).toContain('IsActive = 1');
    expect(TEST_ADMIN_UPSERT_SQL).not.toMatch(/@.*@example/);
    const p = testAdminParams({ name: "O'Neil", email: 'a@b.co', oid: testAdminOid('a@b.co') });
    expect(Object.keys(p).sort()).toEqual(['email', 'name', 'oid']);
    for (const k of ['@oid', '@name', '@email']) expect(TEST_ADMIN_UPSERT_SQL).toContain(k);
  });
});

describe('test-login picker: DB is the source of truth for the named Admin', () => {
  const adminOid = testAdminOid('person@example.com');
  const rows: TestUserRow[] = [
    { userId: 1, oid: TEST_USER_OIDS[0]!, name: 'Test Admin', email: null, role: 'Admin', teamId: null, teamName: null },
    { userId: 9, oid: adminOid, name: 'Test Person', email: 'person@example.com', role: 'Admin', teamId: null, teamName: null },
    { userId: 50, oid: '99999999-aaaa-4bbb-8ccc-dddddddddddd', name: 'Real Employee', email: 'real@corp.example', role: 'Admin', teamId: null, teamName: null },
  ];
  /** Behaves like the SQL repository: seed list OR reserved range, active DB rows only; arbitrary oids are never found. */
  const fakeRepo = (): TestUserRepo => ({
    list: vi.fn(async (oids: readonly string[]) => rows.filter((r) => oids.includes(r.oid) || r.oid.startsWith(TEST_OID_PREFIX))),
    find: vi.fn(async (oid: string, allowed: readonly string[]) => ((allowed.includes(oid) || /^00000000-0000-4000-8000-[0-9a-f]{12}$/.test(oid)) ? (rows.find((r) => r.oid === oid && (allowed.includes(oid) || oid.startsWith(TEST_OID_PREFIX))) ?? null) : null)),
  });
  const unlock = async (agent: ReturnType<typeof request.agent>) => {
    const csrf = await getCsrf(agent, '/auth/test-login');
    return agent.post('/auth/test-login').type('form').send({ _csrf: csrf, code: 'correct-horse-battery-staple' });
  };
  it.each([[{ TEST_ADMIN_NAME: 'Test Person', TEST_ADMIN_EMAIL: 'person@example.com' }], [{}]])('offers the named Admin whether or not TEST_ADMIN_* is in the runtime env (%j)', async (env) => {
    const repo = fakeRepo();
    const { app, store } = makeApp({ config: testAccessConfig(env), testUserRepo: repo });
    const agent = request.agent(app);
    const page = await unlock(agent);
    expect(page.text).toContain('Test Person');
    expect(page.text).not.toContain('Real Employee');
    expect(repo.list).toHaveBeenCalledWith(TEST_USER_OIDS);
    const csrf = await getCsrf(agent, '/auth/test-login');
    expect((await agent.post('/auth/test-login/user').type('form').send({ _csrf: csrf, oid: adminOid })).status).toBe(303);
    const [data] = Object.values(await allSessions(store));
    expect(data?.user).toMatchObject({ userId: 9, role: 'Admin', oid: adminOid, teamId: null });
    expect((await agent.get('/settings/general')).status).toBe(200);
  });
  it('a tampered form cannot pick a real user, an unknown reserved oid, or anything else', async () => {
    const { app } = makeApp({ config: testAccessConfig(), testUserRepo: fakeRepo() });
    const agent = request.agent(app);
    await unlock(agent);
    const csrf = await getCsrf(agent, '/auth/test-login');
    for (const oid of ['99999999-aaaa-4bbb-8ccc-dddddddddddd', `${TEST_OID_PREFIX}ffffffffffff`, '12345678-1234-4234-8234-123456789012', testAdminOid('someone-else@example.com'), 'not-a-guid', '']) {
      expect((await agent.post('/auth/test-login/user').type('form').send({ _csrf: csrf, oid })).status, oid).toBe(403);
    }
    expect((await agent.get('/')).status).toBe(303);
  });
  it('createTestLoginRouter is still exported', () => {
    expect(typeof createTestLoginRouter).toBe('function');
  });
});

describe('testUserRepo SQL', () => {
  it('restricts to active users in the seed list or the reserved range, and find() re-checks the range', async () => {
    const calls: Array<{ text: string; params: Array<{ name: string; value: unknown }> }> = [];
    vi.resetModules();
    vi.doMock('../src/db/system', () => ({ systemQuery: async (text: string, params: Array<{ name: string; value: unknown }>) => { calls.push({ text, params }); return []; } }));
    const { testUserRepo } = await import('../src/db/testUserRepo.js');
    await testUserRepo.list(TEST_USER_OIDS);
    expect(calls[0]!.text).toContain('u.IsActive = 1');
    expect(calls[0]!.text).toContain(`LIKE '${TEST_OID_PREFIX}%'`);
    expect(await testUserRepo.find('12345678-1234-4234-8234-123456789012', TEST_USER_OIDS)).toBeNull();
    expect(calls).toHaveLength(1); // never even queried
    await testUserRepo.find(`${TEST_OID_PREFIX}abcdef012345`, TEST_USER_OIDS);
    expect(calls[1]!.text).toContain('u.EntraOid = TRY_CAST(@oid AS uniqueidentifier)');
    expect(calls[1]!.text).toContain(`LIKE '${TEST_OID_PREFIX}%'`);
    vi.doUnmock('../src/db/system');
  });
});
