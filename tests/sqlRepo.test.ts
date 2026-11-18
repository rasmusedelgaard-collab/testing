import { beforeEach, describe, expect, it, vi } from 'vitest';

const h = vi.hoisted(() => {
  const calls: Array<{ text: string; params: Record<string, unknown> }> = [];
  const script: Array<(c: { text: string; params: Record<string, unknown> }) => unknown> = [];
  class Request {
    params: Record<string, unknown> = {};
    constructor(public tx: unknown) {}
    input(n: string, _t: unknown, v: unknown) { this.params[n] = v; return this; }
    async query(text: string) {
      const c = { text, params: this.params };
      calls.push(c);
      const res = script.shift()?.(c);
      const sets = (res as unknown[][] | undefined) ?? [[]];
      return { recordset: sets[0], recordsets: sets };
    }
  }
  return { calls, script, Request };
});
vi.mock('mssql', () => ({
  default: { Request: h.Request, Int: 'Int', Bit: 'Bit', Char: () => 'Char', VarChar: () => 'VarChar', NVarChar: () => 'NVarChar', Decimal: () => 'Decimal', DateTime2: () => 'DT2', UniqueIdentifier: 'Guid', MAX: 0 },
}));
vi.mock('../src/db/withRlsContext', () => ({ withRlsContext: async (_c: unknown, fn: (tx: unknown) => unknown) => fn({}) }));

import { SqlRepo } from '../src/db/sqlRepo';
import { filterClause } from '../src/db/crmSql';

const ctx = { userId: 3, role: 'User' as const, teamId: 1 };
const meta = { userId: 3, correlationId: '11111111-1111-4111-8111-111111111111' };
const repo = () => new SqlRepo({} as never, ctx);
beforeEach(() => { h.calls.length = 0; h.script.length = 0; });

describe('SqlRepo', () => {
  it('create: derives TeamId from the owner row, inserts, then writes a C audit row in the same transaction', async () => {
    h.script.push(() => [[{ Id: 42, OwnerId: 3, TeamId: 1 }]], () => [[]]);
    const id = await repo().create('accounts', { Name: 'Acme', OwnerId: 3, TeamId: 99 }, meta);
    expect(id).toBe(42);
    expect(h.calls[0]!.text).toContain('DECLARE @TeamId int = (SELECT TeamId FROM crm.Users WHERE UserId = @OwnerId)');
    expect(h.calls[0]!.text).toContain('INSERT crm.Accounts');
    expect(h.calls[0]!.params).not.toHaveProperty('TeamId');
    expect(h.calls[0]!.params).toMatchObject({ Name: 'Acme', OwnerId: 3, By: 3 });
    expect(h.calls[1]!.text).toContain('INSERT crm.AuditLog');
    expect(h.calls[1]!.params).toMatchObject({ act: 'C', nv: 'Acme', owner: 3, team: 1, by: 3, cid: meta.correlationId });
  });
  it('update: guarded by RowVer, uses OUTPUT deleted/inserted, audits only changed audited fields (stage by name)', async () => {
    h.script.push(
      () => [[{ o_Amount: 10, n_Amount: 20, o_StageId: 1, n_StageId: 2, o_CurrencyCode: 'DKK', n_CurrencyCode: 'DKK', o_CloseDate: '2026-01-01', n_CloseDate: '2026-01-01', o_OwnerId: 3, n_OwnerId: 3, n_TeamId: 1 }]],
      () => [[{ StageId: 1, Name: 'Qual' }, { StageId: 2, Name: 'Proposal' }]],
      () => [[]], () => [[]],
    );
    const r = await repo().update('opportunities', 5, { Name: 'x', OwnerId: 3 }, '0x0000000000000001', meta);
    expect(r).toBe('ok');
    const t = h.calls[0]!.text;
    expect(t).toContain('RowVer = CONVERT(binary(8), @rv, 1)');
    expect(t).toContain('OUTPUT deleted.Amount AS o_Amount');
    expect(t).toContain('TeamId = (SELECT TeamId FROM crm.Users WHERE UserId = @OwnerId)');
    const audits = h.calls.filter((c) => c.text.includes('INSERT crm.AuditLog')).map((c) => [c.params.fn, c.params.ov, c.params.nv]);
    expect(audits).toEqual([['Amount', '10', '20'], ['StageId', 'Qual', 'Proposal']]);
  });
  it('update: no row updated + row still exists => conflict (409); no row => notfound (404)', async () => {
    h.script.push(() => [[]], () => [[{ ok: 1 }]]);
    expect(await repo().update('accounts', 1, { Name: 'x', OwnerId: 3 }, '0x0000000000000001', meta)).toBe('conflict');
    expect(h.calls[1]!.text).toMatch(/SELECT 1 AS ok FROM crm\.Accounts WHERE AccountId = @id/);
    h.script.push(() => [[]], () => [[]]);
    expect(await repo().update('accounts', 1, { Name: 'x', OwnerId: 3 }, '0x0000000000000001', meta)).toBe('notfound');
  });
  it('update: a malformed RowVer never reaches the database', async () => {
    expect(await repo().update('accounts', 1, { OwnerId: 3 }, "0x1'; DROP TABLE x;--", meta)).toBe('conflict');
    expect(h.calls).toHaveLength(0);
  });
  it('remove account: nulls visible links first, then DELETE ... OUTPUT, then a D audit row', async () => {
    h.script.push(() => [[]], () => [[]], () => [[{ OwnerId: 3, TeamId: 1, Label: 'Acme' }]], () => [[]]);
    expect(await repo().remove('accounts', 9, meta)).toBe(true);
    expect(h.calls.map((c) => c.text.split(' ').slice(0, 3).join(' '))).toEqual(['UPDATE crm.Contacts SET', 'UPDATE crm.Activities SET', 'DELETE crm.Accounts OUTPUT', 'INSERT crm.AuditLog (EntityType,']);
    expect(h.calls[3]!.params).toMatchObject({ act: 'D', ov: 'Acme', owner: 3 });
  });
  it('remove: hidden row (0 rows deleted) => false and no audit row', async () => {
    h.script.push(() => [[]], () => [[]], () => [[]]);
    expect(await repo().remove('accounts', 9, meta)).toBe(false);
    expect(h.calls.some((c) => c.text.includes('AuditLog'))).toBe(false);
  });
  it('list: whitelisted sort, parameterised filters and search with LIKE-escaping', async () => {
    h.script.push(() => [[{ Total: 1 }], [{ Id: 1 }]]);
    const res = await repo().list('opportunities', { q: '50%_off', page: 3, pageSize: 25, sort: 'amount', dir: 'desc', today: '2026-11-18', filters: { status: 'open', owner: '4', from: '2026-01-01' } });
    expect(res.total).toBe(1);
    const c = h.calls[0]!;
    expect(c.text).toContain('ORDER BY e.Amount DESC');
    expect(c.text).toContain('s.StageType = @f_status');
    expect(c.text).toContain('e.OwnerId = @f_owner');
    expect(c.text).toContain('OFFSET @off ROWS FETCH NEXT @ps ROWS ONLY');
    expect(c.params).toMatchObject({ q: '%50\\%\\_off%', f_owner: 4, f_status: 'open', f_from: '2026-01-01', off: 50, ps: 25 });
    expect(c.text).not.toContain('50%');
  });
  it('owners: Admin gets every active user; others self + active team-mates only', async () => {
    h.script.push(() => [[]], () => [[]]);
    await new SqlRepo({} as never, { userId: 1, role: 'Admin', teamId: null }).owners();
    await repo().owners();
    expect(h.calls[0]!.text).toContain('WHERE u.IsActive = 1');
    expect(h.calls[1]!.text).toContain('u.UserId = @me OR (u.IsActive = 1 AND @team IS NOT NULL AND u.TeamId = @team)');
    expect(h.calls[1]!.params).toMatchObject({ me: 3, team: 1 });
  });
  it('get/refExists/setDone use parameters only', async () => {
    h.script.push(() => [[{ Id: 1 }]], () => [[{ ok: 1 }]], () => [[{ Id: 1 }]]);
    expect(await repo().get('contacts', 1)).toMatchObject({ Id: 1 });
    expect(await repo().refExists('accounts', 1)).toBe(true);
    expect(await repo().setDone(1, true, meta)).toBe(true);
    for (const c of h.calls) expect(c.text).not.toMatch(/\b1\b\s*$/);
  });
  it('history: parameterised by entity type and id, resolves owner ids to names', async () => {
    h.script.push(() => [[{ At: '2026-11-18T10:00:00.000Z', ByName: 'Jane', Action: 'U', FieldName: 'OwnerId', OldValue: 'Anna', NewValue: 'Jane' }]]);
    const rows = await repo().history('accounts', 7);
    expect(rows).toEqual([{ at: '2026-11-18T10:00:00.000Z', by: 'Jane', action: 'U', field: 'OwnerId', oldValue: 'Anna', newValue: 'Jane' }]);
    expect(h.calls[0]!.text).toContain("a.FieldName = 'OwnerId'");
    expect(h.calls[0]!.params).toMatchObject({ et: 'account', id: 7 });
  });
  it('filterClause: unknown keys yield no SQL; enum values are never echoed', () => {
    expect(filterClause('accounts', 'bogus', 'x')).toBeNull();
    expect(filterClause('activities', 'due', "'; DROP TABLE x;--")).toBeNull();
    expect(filterClause('activities', 'due', 'overdue')).toContain('@today');
    expect(filterClause('opportunities', 'status', 'all')).toBeNull();
  });
});
