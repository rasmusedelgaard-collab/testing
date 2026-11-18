import { beforeEach, describe, expect, it, vi } from 'vitest';

const h = vi.hoisted(() => {
  const state = {
    events: [] as string[],
    queries: [] as Array<{ text: string; inputs: Array<{ name: string; type: unknown; value: unknown }> }>,
    beginError: null as unknown,
    queryError: null as unknown,
    commitError: null as unknown,
  };
  class Request {
    inputs: Array<{ name: string; type: unknown; value: unknown }> = [];
    constructor(public tx: unknown) {}
    input(name: string, type: unknown, value: unknown) {
      this.inputs.push({ name, type, value });
      return this;
    }
    async query(text: string) {
      state.events.push('query');
      state.queries.push({ text, inputs: this.inputs });
      if (state.queryError) {
        const e = state.queryError;
        state.queryError = null; // only the first query fails
        throw e;
      }
      return { recordset: [] };
    }
  }
  class Transaction {
    constructor(public pool: unknown) {}
    async begin(iso: unknown) {
      state.events.push(`begin:${String(iso)}`);
      if (state.beginError) throw state.beginError;
    }
    async commit() {
      state.events.push('commit');
      if (state.commitError) throw state.commitError;
    }
    async rollback() {
      state.events.push('rollback');
    }
  }
  return { state, Request, Transaction };
});

vi.mock('mssql', () => ({
  default: {
    Request: h.Request,
    Transaction: h.Transaction,
    Int: 'Int',
    NVarChar: (n: number) => `NVarChar(${n})`,
    ISOLATION_LEVEL: { READ_COMMITTED: 2 },
  },
}));
vi.mock('../src/db/pool', () => ({ getRlsPool: async () => ({}) }));
vi.mock('../src/db/retry', async (orig) => {
  const real = await orig<typeof import('../src/db/retry')>();
  return { ...real, withDbRetry: <T>(fn: () => Promise<T>) => real.withDbRetry(fn, { sleep: async () => undefined, budgetMs: 5000 }) };
});

import { ConflictError, DbUnavailableError, ForbiddenError, ValidationError } from '../src/errors';
import { SET_CONTEXT_BATCH, withRlsContext } from '../src/db/withRlsContext';

const ctx = { userId: 7, role: 'User' as const, teamId: 3 };
const sqlErr = (number: number) => Object.assign(new Error(`sql ${number}`), { number });

beforeEach(() => {
  h.state.events.length = 0;
  h.state.queries.length = 0;
  h.state.beginError = null;
  h.state.queryError = null;
  h.state.commitError = null;
});

describe('withRlsContext', () => {
  it('begins a READ_COMMITTED transaction, sets context first, runs fn, then commits', async () => {
    const result = await withRlsContext(ctx, async () => {
      h.state.events.push('fn');
      return 42;
    });
    expect(result).toBe(42);
    expect(h.state.events).toEqual(['begin:2', 'query', 'fn', 'commit']);
  });

  it('sends one parameterised batch: guard, then UserId/Role/TeamId, all read-only', () => {
    expect(SET_CONTEXT_BATCH.indexOf('THROW 51000')).toBeGreaterThan(-1);
    expect(SET_CONTEXT_BATCH.indexOf('THROW 51000')).toBeLessThan(SET_CONTEXT_BATCH.indexOf('sp_set_session_context'));
    for (const key of ['UserId', 'Role', 'TeamId']) {
      expect(SET_CONTEXT_BATCH).toMatch(new RegExp(`@key = N'${key}',\\s+@value = @${key},\\s+@read_only = 1`));
    }
    expect(SET_CONTEXT_BATCH).not.toMatch(/@read_only = 0/);
  });

  it('binds the identity as parameters, never in the SQL text', async () => {
    await withRlsContext({ userId: 7, role: 'Manager', teamId: null }, async () => undefined);
    const q = h.state.queries[0]!;
    expect(q.text).toBe(SET_CONTEXT_BATCH);
    expect(q.inputs).toEqual([
      { name: 'UserId', type: 'Int', value: 7 },
      { name: 'Role', type: 'NVarChar(10)', value: 'Manager' },
      { name: 'TeamId', type: 'Int', value: null },
    ]);
  });

  it('rejects an invalid context before any database call', async () => {
    await expect(withRlsContext({ userId: 0, role: 'User', teamId: null }, async () => 1)).rejects.toThrow();
    await expect(withRlsContext({ userId: 1, role: 'Root' as never, teamId: null }, async () => 1)).rejects.toThrow();
    expect(h.state.events).toEqual([]);
  });

  it('rolls back (releasing the connection) when fn throws, and rethrows', async () => {
    await expect(withRlsContext(ctx, async () => { throw new Error('boom'); })).rejects.toThrow('boom');
    expect(h.state.events).toEqual(['begin:2', 'query', 'rollback']);
  });

  it('rolls back when commit fails', async () => {
    h.state.commitError = sqlErr(3999);
    await expect(withRlsContext(ctx, async () => 1)).rejects.toThrow();
    expect(h.state.events.at(-1)).toBe('rollback');
  });

  it('refuses to run fn when the stale-context guard fires (51000), and rolls back', async () => {
    h.state.queryError = sqlErr(51000);
    const fn = vi.fn(async () => 1);
    await expect(withRlsContext(ctx, fn)).rejects.toMatchObject({ number: 51000 });
    expect(fn).not.toHaveBeenCalled();
    expect(h.state.events).toEqual(['begin:2', 'query', 'rollback']);
  });

  it.each([
    [33504, ForbiddenError],
    [547, ConflictError],
    [2627, ValidationError],
    [2601, ValidationError],
  ] as const)('maps SQL error %i inside fn to the right application error', async (n, Cls) => {
    await expect(withRlsContext(ctx, async () => { throw sqlErr(n); })).rejects.toBeInstanceOf(Cls);
    expect(h.state.events.at(-1)).toBe('rollback');
  });

  it('retries only the begin/context step on a transient error, then succeeds', async () => {
    h.state.beginError = sqlErr(40613);
    let attempts = 0;
    const realBegin = h.Transaction.prototype.begin;
    vi.spyOn(h.Transaction.prototype, 'begin').mockImplementation(async function (this: unknown, iso: unknown) {
      attempts++;
      if (attempts >= 3) h.state.beginError = null;
      return realBegin.call(this, iso);
    });
    const fn = vi.fn(async () => 'done');
    await expect(withRlsContext(ctx, fn)).resolves.toBe('done');
    expect(attempts).toBe(3);
    expect(fn).toHaveBeenCalledTimes(1);
    vi.restoreAllMocks();
  });

  it('does NOT replay fn after a transient error inside it: 503 and rollback', async () => {
    const fn = vi.fn(async () => { throw sqlErr(40613); });
    await expect(withRlsContext(ctx, fn)).rejects.toBeInstanceOf(DbUnavailableError);
    expect(fn).toHaveBeenCalledTimes(1);
    expect(h.state.events.at(-1)).toBe('rollback');
  });
});
