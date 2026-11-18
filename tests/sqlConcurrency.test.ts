import request from 'supertest';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * A fake mssql that behaves like the real driver in the one way the memory store cannot: a Transaction owns ONE
 * connection, and a second request while one is running fails with EREQINPROG (and a rollback during a request fails too,
 * leaking the connection). Every CRM page is then driven through the real SqlRepo/SqlAdminRepo + withRlsContext.
 */
const h = vi.hoisted(() => {
  const state = { begun: 0, committed: 0, rolledBack: 0, released: 0, concurrent: 0, statements: 0, closed: 0, poolReleased: 0 };
  class FakeTransaction {
    active: unknown = null;
    done = false;
    _acquiredConnection: { close(): void } | null = null;
    constructor(public pool: unknown) {}
    async begin() { state.begun++; this._acquiredConnection = { close: () => { state.closed++; } }; }
    async commit() {
      if (this.active) throw Object.assign(new Error("Can't commit transaction. There is a request in progress."), { code: 'EREQINPROG' });
      this.done = true; this._acquiredConnection = null; state.committed++; state.released++;
    }
    async rollback() {
      if (this.active) throw Object.assign(new Error("Can't rollback transaction. There is a request in progress."), { code: 'EREQINPROG' });
      this.done = true; this._acquiredConnection = null; state.rolledBack++; state.released++;
    }
  }
  class FakeRequest {
    params: Record<string, unknown> = {};
    constructor(public tx: FakeTransaction) {}
    input(n: string, _t: unknown, v: unknown) { this.params[n] = v; return this; }
    async query(text: string) {
      if (this.tx.active) {
        state.concurrent++;
        throw Object.assign(new Error("Can't acquire connection for the request. There is another request in progress."), { code: 'EREQINPROG' });
      }
      this.tx.active = this;
      state.statements++;
      await new Promise((r) => setImmediate(r)); // let any concurrent caller overlap
      this.tx.active = null;
      if (/SELECT SettingKey, SettingValue FROM crm.Settings;/.test(text)) {
        return { recordset: [], recordsets: [[{ SettingKey: 'locale.code', SettingValue: 'en-GB' }, { SettingKey: 'locale.timezone', SettingValue: 'UTC' }, { SettingKey: 'currency.base', SettingValue: 'DKK' }, { SettingKey: 'currency.enabled', SettingValue: 'DKK,EUR' }], [{ StageId: 1, Name: 'Q', SortOrder: 1, Probability: 10, StageType: 'open' }, { StageId: 5, Name: 'W', SortOrder: 5, Probability: 100, StageType: 'won' }, { StageId: 6, Name: 'L', SortOrder: 6, Probability: 0, StageType: 'lost' }], []] };
      }
      return { recordset: [], recordsets: [[], []] };
    }
  }
  return { state, FakeTransaction, FakeRequest };
});

vi.mock('mssql', () => ({
  default: {
    Transaction: h.FakeTransaction, Request: h.FakeRequest, ISOLATION_LEVEL: { READ_COMMITTED: 2 },
    Int: 'Int', Bit: 'Bit', SmallInt: 'SmallInt', TinyInt: 'TinyInt', MAX: 0, UniqueIdentifier: 'Guid',
    Char: () => 'Char', VarChar: () => 'VarChar', NVarChar: () => 'NVarChar', Decimal: () => 'Decimal', VarBinary: () => 'VarBinary', DateTime2: () => 'DT2',
  },
}));
vi.mock('../src/db/pool', () => ({ getRlsPool: async () => ({ release: () => { h.state.poolReleased++; } }), getSystemPool: async () => ({}), closePools: async () => undefined }));

import { createMemoryStore, memoryBranding } from '../src/db/memoryStore';
import { sqlStore } from '../src/db/sqlRepo';
import { serial } from '../src/db/txQueue';
import { withRlsContext } from '../src/db/withRlsContext';
import { demoConfig, makeApp } from './helpers';

beforeEach(() => { Object.assign(h.state, { begun: 0, committed: 0, rolledBack: 0, released: 0, concurrent: 0, statements: 0, closed: 0, poolReleased: 0 }); });

const newTx = () => new h.FakeTransaction({});

describe('the fake driver is faithful', () => {
  it('rejects a second concurrent request on one transaction with EREQINPROG', async () => {
    const tx = newTx();
    const results = await Promise.allSettled([new h.FakeRequest(tx).query('a'), new h.FakeRequest(tx).query('b')]);
    expect(results.map((r) => r.status)).toEqual(['fulfilled', 'rejected']);
    expect((results[1] as PromiseRejectedResult).reason.code).toBe('EREQINPROG');
  });
  it('serial() queues concurrent callers so none of them fails, in order, and survives a failing predecessor', async () => {
    const tx = newTx();
    const order: string[] = [];
    const run = (name: string, fail = false) => serial(tx, async () => { order.push(`start ${name}`); await new h.FakeRequest(tx).query(name); order.push(`end ${name}`); if (fail) throw new Error('boom'); });
    const out = await Promise.allSettled([run('a'), run('b', true), run('c')]);
    expect(out.map((o) => o.status)).toEqual(['fulfilled', 'rejected', 'fulfilled']);
    expect(order).toEqual(['start a', 'end a', 'start b', 'end b', 'start c', 'end c']);
    expect(h.state.concurrent).toBe(0);
  });
});

describe('defence in depth: even concurrent repository calls cannot overlap on a transaction', () => {
  it('Promise.all over SqlRepo methods inside withRlsContext succeeds (the queue serialises them)', async () => {
    const out = await sqlStore.run({ userId: 1, role: 'Admin', teamId: null }, (repo) => Promise.all([repo.lookups(), repo.owners(), repo.fxRates('DKK'), repo.activityCounts('2026-01-01'), repo.oppAggregates({ types: ['open'], groupBy: ['stage'] })]));
    expect(out).toHaveLength(5);
    expect(h.state.concurrent).toBe(0);
    const admin = await sqlStore.runAdmin({ userId: 1, role: 'Admin', teamId: null }, (a) => Promise.all([a.settingsMap(), a.fxRates(), a.teams(), a.users()]));
    expect(admin).toHaveLength(4);
    expect(h.state.concurrent).toBe(0);
  });
});

describe('every page through the real SQL repositories on the strict fake', () => {
  async function session(role: 'Admin' | 'Manager' | 'User') {
    const { app } = makeApp({ config: demoConfig(), store: sqlStore, branding: memoryBranding(createMemoryStore().state) });
    const agent = request.agent(app);
    await agent.get(`/__test/login?role=${role}`);
    return agent;
  }
  const pages = [
    '/', '/analytics', '/analytics?period=custom&from=2026-01-01&to=2026-12-31&owner=3',
    '/accounts', '/contacts', '/opportunities', '/activities',
    '/accounts?q=a&sort=name&owner=2', '/opportunities?status=closed&from=2026-01-01&to=2026-12-31', '/activities?status=open&type=actionable&due=due',
    '/accounts/new', '/contacts/new', '/opportunities/new', '/activities/new', '/accounts/1', '/opportunities/1/edit', '/accounts/1/delete',
    '/settings/general', '/settings/locale', '/settings/fx', '/settings/stages', '/settings/picklists/industry', '/settings/visibility', '/settings/users',
  ];
  it.each(['Admin', 'Manager', 'User'] as const)('%s: no page ever issues overlapping requests on a transaction, and none answers 500', async (role) => {
    const agent = await session(role);
    for (const p of pages) {
      const res = await agent.get(p);
      const expectOk = role === 'Admin' || !p.startsWith('/settings');
      expect([200, 404, 403], `${p} -> ${res.status}`).toContain(res.status);
      if (expectOk && !/\/\d+/.test(p) && !p.includes('picklists')) expect(res.status, p).toBe(200);
    }
    expect(h.state.concurrent).toBe(0);
    expect(h.state.statements).toBeGreaterThan(20);
  });
  it('every transaction is finished and its connection released (no leak across many page loads)', async () => {
    const agent = await session('Admin');
    for (let i = 0; i < 30; i++) await agent.get(i % 2 ? '/' : '/analytics');
    expect(h.state.begun).toBeGreaterThanOrEqual(30);
    expect(h.state.released).toBe(h.state.begun);
    expect(h.state.committed + h.state.rolledBack).toBe(h.state.begun);
  });
  it('POST flows (create, update, delete, settings) also stay strictly sequential', async () => {
    const agent = await session('Admin');
    const token = async () => /name="_csrf" value="([^"]+)"/.exec((await agent.get('/')).text)![1]!;
    for (const [path, body] of [['/accounts', { Name: 'x', OwnerId: '1' }], ['/settings/general', { appName: 'X', accentColor: '#1D4ED8' }], ['/settings/visibility', { mode: 'team', confirm: '1' }], ['/settings/stages', { n: '0', action: 'save' }], ['/accounts/1/delete', {}]] as const) {
      const res = await agent.post(path).type('form').send({ _csrf: await token(), ...body });
      expect(res.status, path).not.toBe(500);
    }
    expect(h.state.concurrent).toBe(0);
  });
});

describe('withRlsContext releases the connection even when rollback fails', () => {
  it('a request still in flight when fn throws: rollback fails (EREQINPROG), so the connection is closed and handed back, never leaked', async () => {
    await expect(withRlsContext({ userId: 1, role: 'Admin', teamId: null }, async (tx) => {
      void new h.FakeRequest(tx as never).query('never awaited'); // still running when we throw
      throw new Error('boom');
    })).rejects.toThrow('boom');
    expect(h.state.rolledBack).toBe(0); // the rollback itself failed
    expect(h.state.closed).toBe(1);
    expect(h.state.poolReleased).toBe(1);
  });
  it('a normal failure still rolls back cleanly without discarding the connection', async () => {
    await expect(withRlsContext({ userId: 1, role: 'Admin', teamId: null }, async () => { throw new Error('boom'); })).rejects.toThrow('boom');
    expect(h.state.rolledBack).toBe(1);
    expect(h.state.closed).toBe(0);
    expect(h.state.poolReleased).toBe(0);
  });
});
