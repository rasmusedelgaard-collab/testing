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
      const sets = (script.shift()?.(c) as unknown[][] | undefined) ?? [[]];
      return { recordset: sets[0], recordsets: sets };
    }
  }
  return { calls, script, Request };
});
vi.mock('mssql', () => ({
  default: { Request: h.Request, Int: 'Int', Bit: 'Bit', SmallInt: 'SmallInt', TinyInt: 'TinyInt', Char: () => 'Char', VarChar: () => 'VarChar', NVarChar: () => 'NVarChar', Decimal: () => 'Decimal', VarBinary: () => 'VarBinary', UniqueIdentifier: 'Guid', MAX: 0 },
}));
vi.mock('../src/db/withRlsContext', () => ({ withRlsContext: async (_c: unknown, fn: (tx: unknown) => unknown) => fn({}) }));

import { SqlAdminRepo } from '../src/db/sqlAdmin';

const meta = { userId: 1, correlationId: '11111111-1111-4111-8111-111111111111' };
const repo = () => new SqlAdminRepo({} as never, { userId: 1, role: 'Admin', teamId: null });
const verbs = () => h.calls.map((c) => c.text.trim().split(/\s+/).slice(0, 2).join(' '));
beforeEach(() => { h.calls.length = 0; h.script.length = 0; });

describe('SqlAdminRepo', () => {
  it('saveSettings: updates only changed values and writes one audit row each (OwnerId = ChangedBy, TeamId NULL)', async () => {
    h.script.push(() => [[{ SettingId: 5, SettingValue: 'CRM' }]], () => [[]], () => [[]], () => [[{ SettingId: 6, SettingValue: '#111111' }]], () => [[]]);
    await repo().saveSettings({ 'app.name': 'Acme', 'branding.accentColor': '#111111' }, meta);
    expect(verbs()).toEqual(['SELECT SettingId,', 'UPDATE crm.Settings', 'INSERT crm.AuditLog', 'SELECT SettingId,']);
    const a = h.calls[2]!;
    expect(a.text).toContain('@by, NULL, @by');
    expect(a.params).toMatchObject({ et: 'setting', eid: 5, act: 'U', fn: 'app.name', ov: 'CRM', nv: 'Acme', by: 1 });
  });
  it('saveStages: deletes first, then resets types/names in one statement, then writes final values in order', async () => {
    h.script.push(
      () => [[{ StageId: 1, Name: 'A', SortOrder: 1, Probability: 10, StageType: 'open', Used: 0 }, { StageId: 2, Name: 'B', SortOrder: 2, Probability: 100, StageType: 'won', Used: 0 }, { StageId: 3, Name: 'C', SortOrder: 3, Probability: 0, StageType: 'lost', Used: 0 }]],
    );
    const r = await repo().saveStages([{ id: 2, name: 'B2', probability: 100, type: 'won' }, { id: 1, name: 'A', probability: 20, type: 'open' }, { id: null, name: 'N', probability: 5, type: 'open' }], meta);
    expect(r.ok).toBe(true);
    const v = verbs();
    expect(v.slice(0, 4)).toEqual(['SELECT s.StageId,', 'DELETE crm.PipelineStages', 'INSERT crm.AuditLog', 'UPDATE crm.PipelineStages']);
    expect(h.calls[3]!.text).toContain("StageType = 'open', Name = CONCAT(N'~'");
    const updates = h.calls.filter((c) => c.text.startsWith('UPDATE crm.PipelineStages SET Name = @n'));
    expect(updates.map((u) => [u.params.id, u.params.o])).toEqual([[2, 1], [1, 2]]);
  });
  it('saveStages: refuses to delete a stage that opportunities use, and changes nothing', async () => {
    h.script.push(() => [[{ StageId: 1, Name: 'A', SortOrder: 1, Probability: 10, StageType: 'open', Used: 3 }]]);
    const r = await repo().saveStages([], meta);
    expect(r).toEqual({ ok: false, error: 'Move 3 opportunities out of "A" before deleting it.' });
    expect(h.calls).toHaveLength(1);
  });
  it('saveFxRate: reports a duplicate instead of overwriting; inserts otherwise with an audit row', async () => {
    h.script.push(() => [[{ FxRateId: 9 }]]);
    expect(await repo().saveFxRate({ id: null, base: 'DKK', currency: 'EUR', month: '2026-11', rate: 7.5 }, meta)).toEqual({ ok: false, duplicateOf: 9 });
    expect(h.calls).toHaveLength(1);
    h.calls.length = 0;
    h.script.push(() => [[]], () => [[{ Id: 4 }]], () => [[]]);
    expect(await repo().saveFxRate({ id: null, base: 'DKK', currency: 'EUR', month: '2026-11', rate: 7.5 }, meta)).toEqual({ ok: true, id: 4 });
    expect(h.calls[1]!.params).toMatchObject({ m: '2026-11-01', r: 7.5, b: 'DKK', c: 'EUR' });
    expect(h.calls[2]!.params).toMatchObject({ et: 'fxrate', act: 'C' });
  });
  it('saveTeam: pre-checks duplicate name and group id, then inserts with an audit row', async () => {
    const teams = [{ TeamId: 1, Name: 'Sales', GroupId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', IsActive: true, Members: 0 }];
    h.script.push(() => [teams]);
    expect(await repo().saveTeam({ id: null, name: 'sales', groupId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' }, meta)).toMatchObject({ ok: false, field: 'name' });
    h.script.push(() => [teams]);
    expect(await repo().saveTeam({ id: null, name: 'Other', groupId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa' }, meta)).toMatchObject({ ok: false, field: 'groupId' });
    h.calls.length = 0;
    h.script.push(() => [teams], () => [[{ Id: 2 }]], () => [[]]);
    expect(await repo().saveTeam({ id: null, name: 'Other', groupId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' }, meta)).toEqual({ ok: true, id: 2 });
    expect(h.calls[1]!.params).toMatchObject({ n: 'Other', g: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' });
  });
  it('saveLogo: updates the existing row, otherwise inserts; both audited, content only as a parameter', async () => {
    h.script.push(() => [[{ Id: 1 }]], () => [[]]);
    await repo().saveLogo('image/png', Buffer.from([1, 2]), 'ab'.repeat(32), meta);
    expect(verbs()).toEqual(['UPDATE crm.BrandingAssets', 'INSERT crm.AuditLog']);
    expect(h.calls[0]!.params.c).toEqual(Buffer.from([1, 2]));
    h.calls.length = 0;
    h.script.push(() => [[]], () => [[{ Id: 2 }]], () => [[]]);
    await repo().saveLogo('image/svg+xml', Buffer.from('<svg/>'), 'cd'.repeat(32), meta);
    expect(verbs()).toEqual(['UPDATE crm.BrandingAssets', 'INSERT crm.BrandingAssets', 'INSERT crm.AuditLog']);
  });
  it('picklist moves renumber the whole list so order is never ambiguous', async () => {
    h.script.push(() => [[{ ValueId: 1, Code: null, Label: 'A', IsActive: true, SortOrder: 5, PName: 'Industry' }, { ValueId: 2, Code: null, Label: 'B', IsActive: true, SortOrder: 5, PName: 'Industry' }]]);
    await repo().movePicklistValue('industry', 2, 'up', meta);
    const ups = h.calls.filter((c) => c.text.startsWith('UPDATE crm.PicklistValues'));
    expect(ups.map((u) => [u.params.id, u.params.o])).toEqual([[2, 1], [1, 2]]);
  });
});
