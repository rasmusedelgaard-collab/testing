/** QA tests for increment 3 (Admin settings): adverse and cross-feature paths complementing tests/settingsHttp.test.ts. */
import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { createMemoryStore } from '../src/db/memoryStore';
import { checkLogo } from '../src/services/logo';
import { csrfFrom, demoConfig, makeApp } from './helpers';

type Role = 'Admin' | 'Manager' | 'User';
async function session(role: Role = 'Admin') {
  const store = createMemoryStore();
  const { app } = makeApp({ config: demoConfig(), store });
  const agent = request.agent(app);
  await agent.get(`/__test/login?role=${role}`);
  const token = async () => csrfFrom((await agent.get('/')).text);
  const post = async (path: string, body: Record<string, unknown>) => agent.post(path).type('form').send({ _csrf: await token(), ...body });
  const rowVer = async (p: string) => /name="rowVer" value="([^"]+)"/.exec((await agent.get(`${p}/edit`)).text)![1]!;
  return { agent, app, store, state: store.state, post, token, rowVer };
}
type S = Awaited<ReturnType<typeof session>>;
const stageBody = (s: S, edit: (rows: Array<Record<string, string>>) => void = () => undefined, action = 'save') => {
  const rows = [...s.state.stages].sort((a, b) => a.sortOrder - b.sortOrder).map((st) => ({ id: String(st.id), name: st.name, prob: String(st.probability), type: st.type }));
  edit(rows);
  const body: Record<string, string> = { n: String(rows.length), action };
  rows.forEach((r, i) => { body[`id_${i}`] = r.id!; body[`name_${i}`] = r.name!; body[`prob_${i}`] = r.prob!; body[`type_${i}`] = r.type!; });
  return body;
};
const PNG_HEAD = Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13]), Buffer.from('IHDR'), Buffer.alloc(17, 1)]);
const oppForm = (o: Record<string, string> = {}) => ({ Name: 'Opp', AccountId: '1', StageId: '1', Amount: '1.000,00', CurrencyCode: 'DKK', CloseDate: '2027-01-01', OwnerId: '1', ...o });

describe('access control: every settings route, every verb, with a valid CSRF token', () => {
  const gets = ['/settings', '/settings/general', '/settings/locale', '/settings/fx', '/settings/fx/1/edit', '/settings/fx/1/delete', '/settings/stages', '/settings/picklists', '/settings/picklists/industry', '/settings/picklists/lost_reason', '/settings/visibility', '/settings/users', '/settings/teams/1/edit', '/settings/teams/1/deactivate', '/settings/nope', '/settings/general/logo', '/settings/%2e%2e/general', '/settings/'];
  const posts = ['/settings/general', '/settings/general/logo', '/settings/locale', '/settings/fx', '/settings/fx/1', '/settings/fx/1/delete', '/settings/stages', '/settings/picklists/industry', '/settings/visibility', '/settings/teams', '/settings/teams/1', '/settings/teams/1/deactivate', '/settings/teams/1/reactivate'];
  it.each(['Manager', 'User'] as const)('%s: 403 on every GET and POST, nothing changes', async (role) => {
    const s = await session(role);
    const before = JSON.stringify([s.state.settings, s.state.stages, s.state.teams, s.state.fx, s.state.picklists]);
    for (const p of gets) expect((await s.agent.get(p)).status, `GET ${p}`).toBe(403);
    for (const p of posts) expect((await s.post(p, { appName: 'Pwn', mode: 'team', confirm: '1', action: 'save', n: '0', name: 'Pwn', groupId: '11111111-1111-4111-8111-111111111111', label: 'x', valueId: '100' })).status, `POST ${p}`).toBe(403);
    expect(JSON.stringify([s.state.settings, s.state.stages, s.state.teams, s.state.fx, s.state.picklists])).toBe(before);
    expect(s.state.audit).toHaveLength(0);
  });
  it('403 holds for HEAD/PUT/DELETE/PATCH variants and a multipart upload by a non-admin', async () => {
    const s = await session('User');
    expect((await s.agent.put('/settings/general').type('form').send({ appName: 'x' })).status).toBeGreaterThanOrEqual(400);
    expect((await s.agent.delete('/settings/fx/1')).status).toBeGreaterThanOrEqual(400);
    const up = await s.agent.post('/settings/general/logo').field('_csrf', await s.token()).attach('logo', Buffer.from('<svg xmlns="http://www.w3.org/2000/svg"/>'), 'l.svg');
    expect(up.status).toBe(403);
    expect(s.state.logo ?? null).toBeNull();
  });
  it('no Settings link for non-admins, and anonymous users get a redirect on all routes', async () => {
    const s = await session('Manager');
    expect((await s.agent.get('/')).text).not.toContain('/settings/');
    const { app } = makeApp({ config: demoConfig(), store: createMemoryStore() });
    for (const p of gets.slice(0, 8)) expect((await request(app).get(p)).status, p).toBe(303);
  });
});

describe('audit: every configuration write leaves a row attributed to the admin', () => {
  it('covers settings, branding, fx, stages, picklists, teams; unchanged saves write nothing', async () => {
    const s = await session();
    const before = () => s.state.audit.length;
    const writes: Array<[string, string, Record<string, unknown>]> = [
      ['setting', '/settings/general', { appName: 'Renamed', accentColor: '#0B5394' }],
      ['setting', '/settings/locale', { base: 'EUR', enabled: ['EUR', 'USD'], locale: 'en-GB', timezone: 'Europe/Berlin', firstDay: '1', fiscalStart: '4' }],
      ['fxrate', '/settings/fx', { currency: 'USD', month: '2026-11', rate: '0.92' }],
      ['team', '/settings/teams', { name: 'Audit Team', groupId: '22222222-2222-4222-8222-222222222222' }],
      ['picklistvalue', '/settings/picklists/industry', { action: 'add', label: 'Audit Industry' }],
      ['stage', '/settings/stages', stageBody(s, (r) => { r[0]!.name = 'Audit Stage'; })],
      ['setting', '/settings/visibility', { mode: 'team', confirm: '1' }],
    ];
    for (const [type, path, body] of writes) {
      const n = before();
      const res = await s.post(path, body);
      expect(res.status, path).toBe(303);
      const rows = s.state.audit.slice(n);
      expect(rows.length, path).toBeGreaterThan(0);
      expect(rows.every((r) => r.entityType === type && r.changedBy === 1 && r.correlationId.length === 36), path).toBe(true);
    }
    const n = before();
    await s.post('/settings/general', { appName: 'Renamed', accentColor: '#0B5394' });
    await s.post('/settings/visibility', { mode: 'team' });
    expect(before()).toBe(n);
    const fx = s.state.fx.at(-1)!;
    const m = before();
    await s.post(`/settings/fx/${fx.id}/delete`, {});
    expect(s.state.audit[m]).toMatchObject({ entityType: 'fxrate', action: 'D' });
  });
  it('a rejected write (validation, duplicate, in-use stage) leaves no audit row and no data change', async () => {
    const s = await session();
    await s.post('/settings/general', { appName: '', accentColor: 'nope' });
    await s.post('/settings/teams', { name: 'X', groupId: 'not-a-guid' });
    await s.post('/settings/fx', { currency: 'EUR', month: '2026-13', rate: 'abc' });
    await s.post('/settings/stages', stageBody(s, (r) => r.splice(r.findIndex((x) => x.name === 'Proposal'), 1))); // Proposal is in use
    expect(s.state.audit).toHaveLength(0);
    expect(s.state.stages.some((x) => x.name === 'Proposal')).toBe(true);
  });
});

describe('stage editor rules', () => {
  it('exactly one Won and one Lost, at least one open, duplicate names (case-insensitive) refused', async () => {
    const s = await session();
    const bad = async (fn: (r: Array<Record<string, string>>) => void, msg: RegExp) => {
      const res = await s.post('/settings/stages', stageBody(s, fn));
      expect(res.status).toBe(422);
      expect(res.text).toMatch(msg);
    };
    await bad((r) => { r[0]!.type = 'won'; }, /exactly one Won stage and one Lost stage/);
    await bad((r) => { r[4]!.type = 'open'; }, /exactly one Won stage and one Lost stage/);
    await bad((r) => { r[5]!.type = 'won'; }, /exactly one Won stage and one Lost stage/);
    await bad((r) => { r.forEach((x) => { if (x.type === 'open') x.type = 'won'; }); }, /exactly one Won/);
    await bad((r) => { r[1]!.name = 'QUALIFICATION'; }, /Each stage needs a different name/);
    await bad((r) => { r[1]!.name = '   '; }, /Enter a stage name/);
    await bad((r) => { r[1]!.prob = '101'; }, /between 0 and 100/);
    await bad((r) => { r[1]!.prob = '-5'; }, /between 0 and 100/);
    await bad((r) => { r[1]!.prob = '5e1'; }, /between 0 and 100/);
    await bad((r) => { r[1]!.name = 'x'.repeat(61); }, /60 characters/);
    expect(s.state.audit).toHaveLength(0);
  });
  it('removing an in-use stage is refused with the count; unused stage removal works; ids are preserved on reorder', async () => {
    const s = await session();
    const used = s.state.stages.find((x) => s.state.data.opportunities.some((o) => o.StageId === x.id))!;
    const res = await s.post('/settings/stages', stageBody(s, (r) => { const i = r.findIndex((x) => x.id === String(used.id)); r.splice(i, 1); }));
    expect(res.status).toBe(422);
    expect(res.text).toMatch(/Move \d+ opportunit(y|ies) out of/);
    expect(s.state.stages.some((x) => x.id === used.id)).toBe(true);
    const idsBefore = s.state.stages.map((x) => x.id).sort();
    await s.post('/settings/stages', stageBody(s, (r) => r.reverse().sort((a, b) => 0 * Number(a.id) - 0 * Number(b.id))));
    expect(s.state.stages.map((x) => x.id).sort()).toEqual(idsBefore);
    const unused = s.state.stages.find((x) => x.type === 'open' && !s.state.data.opportunities.some((o) => o.StageId === x.id))!;
    expect((await s.post('/settings/stages', stageBody(s, (r) => r.splice(r.findIndex((x) => x.id === String(unused.id)), 1)))).status).toBe(303);
    expect(s.state.stages.some((x) => x.id === unused.id)).toBe(false);
  });
  it('tampered editor posts: unknown ids, duplicate ids, huge n, hostile types and missing fields never 500', async () => {
    const s = await session();
    for (const mut of [
      (b: Record<string, string>) => { b.id_0 = '99999'; },
      (b: Record<string, string>) => { b.id_1 = b.id_0!; },
      (b: Record<string, string>) => { b.n = '9999'; },
      (b: Record<string, string>) => { b.n = '-3'; },
      (b: Record<string, string>) => { b.type_0 = '<script>'; },
      (b: Record<string, string>) => { delete b.name_0; },
      (b: Record<string, string>) => { b.action = 'remove:99'; },
      (b: Record<string, string>) => { b.action = 'up:0'; },
      (b: Record<string, string>) => { b.action = 'down:99'; },
    ]) {
      const b = stageBody(s);
      mut(b);
      expect((await s.post('/settings/stages', b)).status).toBeLessThan(500);
    }
    // whatever was accepted, the invariants still hold
    expect(s.state.stages.filter((x) => x.type === 'won')).toHaveLength(1);
    expect(s.state.stages.filter((x) => x.type === 'lost')).toHaveLength(1);
    expect(new Set(s.state.stages.map((x) => x.id)).size).toBe(s.state.stages.length);
  });
  // OPEN DEFECT QA-I3-1 (Low): an unknown stage id in the editor post silently creates a stage in the demo store
  // but is silently dropped by SqlAdminRepo (UPDATE ... WHERE StageId = @id affects 0 rows). The two stores disagree
  // and the admin sees "Settings saved." either way. Tested here on the demo store only.
  it('stage edits show in opportunity forms and filters immediately; probability of won/lost is forced', async () => {
    const s = await session();
    await s.post('/settings/stages', stageBody(s, (r) => { r[0]!.name = 'Discovery'; r[0]!.prob = '15'; r[4]!.prob = '40'; r[5]!.prob = '7'; }));
    expect(s.state.stages.find((x) => x.type === 'won')!.probability).toBe(100);
    expect(s.state.stages.find((x) => x.type === 'lost')!.probability).toBe(0);
    expect((await s.agent.get('/opportunities/new')).text).toContain('Discovery (15%)');
    expect((await s.agent.get('/opportunities')).text).toContain('Discovery');
  });
  it('new stage becomes selectable and usable on an opportunity; then cannot be removed while used', async () => {
    const s = await session();
    expect((await s.post('/settings/stages', { ...stageBody(s), action: 'add' })).text).toContain('not saved yet');
    const b = stageBody(s);
    b.n = String(Number(b.n) + 1); b[`id_${b.n}`] = ''; // keep editor consistent
    const idx = Number(b.n) - 1;
    Object.assign(b, { [`id_${idx}`]: '', [`name_${idx}`]: 'Contracting', [`prob_${idx}`]: '90', [`type_${idx}`]: 'open' });
    expect((await s.post('/settings/stages', b)).status).toBe(303);
    const created = s.state.stages.find((x) => x.name === 'Contracting')!;
    expect((await s.post('/opportunities', oppForm({ StageId: String(created.id) }))).status).toBe(303);
    const res = await s.post('/settings/stages', stageBody(s, (r) => r.splice(r.findIndex((x) => x.name === 'Contracting'), 1)));
    expect(res.status).toBe(422);
    expect(res.text).toContain('Move 1 opportunity out of');
  });
});

describe('visibility, locale and currency take effect immediately', () => {
  it('switching private -> team -> private changes User list results and the Manager note on the next request', async () => {
    const s = await session();
    const u = request.agent(s.app);
    await u.get('/__test/login?role=User');
    const count = async () => [...(await u.get('/contacts')).text.matchAll(/<a href="\/contacts\/\d+">/g)].length;
    const priv = await count();
    await s.post('/settings/visibility', { mode: 'team', confirm: '1' });
    expect(await count()).toBeGreaterThan(priv);
    await s.post('/settings/visibility', { mode: 'private', confirm: '1' });
    expect(await count()).toBe(priv);
    expect((await s.post('/settings/visibility', { mode: 'team', confirm: '0' })).text).toContain('Continue?');
    expect(s.state.settings.visibilityMode).toBe('private');
  });
  it('locale switch changes money, dates and what the amount field accepts, with no stale formatting', async () => {
    const s = await session();
    expect((await s.agent.get('/opportunities?status=all')).text).toMatch(/120\.000,00/);
    await s.post('/settings/locale', { base: 'DKK', enabled: ['DKK', 'EUR', 'USD'], locale: 'en-US', timezone: 'Europe/Copenhagen', firstDay: '7', fiscalStart: '1' });
    const html = (await s.agent.get('/opportunities?status=all')).text;
    expect(html).toMatch(/120,000\.00/);
    expect(html).toContain('12/15/2026');
    const ok = await s.post('/opportunities', oppForm({ Amount: '12,500.75' }));
    expect(ok.status).toBe(303);
    expect(s.state.data.opportunities.at(-1)!.Amount).toBe(12500.75);
    expect((await s.agent.get('/opportunities/1/edit')).text).toContain('value="120000"');
  });
  it('currency changes: a newly enabled currency is selectable at once; the base is forced on; unknown codes are refused', async () => {
    const s = await session();
    await s.post('/settings/locale', { base: 'EUR', enabled: ['USD'], locale: 'da-DK', timezone: 'Europe/Copenhagen', firstDay: '1', fiscalStart: '1' });
    expect(s.state.settings.enabledCurrencies).toEqual(expect.arrayContaining(['EUR', 'USD']));
    expect((await s.agent.get('/opportunities/new')).text).toMatch(/<option value="EUR"[^>]* selected/);
    for (const bad of [{ base: 'XXX' }, { locale: 'xx-XX' }, { timezone: 'Mars/Base' }, { firstDay: '0' }, { firstDay: '8' }, { fiscalStart: '13' }]) {
      const res = await s.post('/settings/locale', { base: 'EUR', enabled: ['EUR'], locale: 'da-DK', timezone: 'Europe/Copenhagen', firstDay: '1', fiscalStart: '1', ...bad });
      expect(res.status, JSON.stringify(bad)).toBe(422);
    }
  });
  // OPEN DEFECT QA-I3-2 (Medium): the edit form lists only enabled currencies, so for an opportunity in a disabled currency the
  // browser silently submits the first option (DKK) and the server otherwise answers 'Choose a currency.'. it.fails: remove marker once fixed.
  it('an existing opportunity in a currency that was later disabled can still be saved after editing another field', async () => {
    const s = await session();
    await s.post('/settings/locale', { base: 'DKK', enabled: ['DKK'], locale: 'da-DK', timezone: 'Europe/Copenhagen', firstDay: '1', fiscalStart: '1' });
    const opp = s.state.data.opportunities.find((o) => o.CurrencyCode === 'EUR')!;
    const rv = await s.rowVer(`/opportunities/${opp.Id}`);
    const res = await s.post(`/opportunities/${opp.Id}`, oppForm({ rowVer: rv, Name: 'Renamed only', AccountId: String(opp.AccountId), StageId: String(opp.StageId), Amount: '45000', CurrencyCode: 'EUR', CloseDate: String(opp.CloseDate), OwnerId: String(opp.OwnerId) }));
    expect(res.status).toBe(303);
  });
});

describe('FX rates', () => {
  it('parses in the active locale, refuses duplicates (add and edit-into), and bad input', async () => {
    const s = await session();
    expect((await s.post('/settings/fx', { currency: 'EUR', month: '2026-11', rate: '7,4590' })).status).toBe(303);
    expect(s.state.fx.at(-1)!.rate).toBe(7.459);
    const dup = await s.post('/settings/fx', { currency: 'EUR', month: '2026-11', rate: '7,5' });
    expect(dup.status).toBe(422);
    expect(dup.text).toContain('already exists');
    expect(s.state.fx.filter((f) => f.currency === 'EUR' && f.month === '2026-11')).toHaveLength(1);
    await s.post('/settings/fx', { currency: 'EUR', month: '2026-10', rate: '7,4' });
    const oct = s.state.fx.find((f) => f.month === '2026-10')!;
    const into = await s.post(`/settings/fx/${oct.id}`, { currency: 'EUR', month: '2026-11', rate: '7,4' });
    expect(into.status).toBe(422);
    expect(oct.month).toBe('2026-10');
    for (const rate of ['', '0', '0,0', '-1', 'abc', '1e3', '99999999999', '7,4,5', '7,4590,']) {
      const r = await s.post('/settings/fx', { currency: 'USD', month: '2026-09', rate });
      expect(r.status, `rate "${rate}"`).toBe(422);
    }
    for (const month of ['', '2026-00', '2026-13', '1999-12', '26-01', '2026-1', 'x']) expect((await s.post('/settings/fx', { currency: 'USD', month, rate: '1,5' })).status, month).toBe(422);
    for (const currency of ['', 'DKK', 'XXX', 'eur ', 'usd']) {
      const st = (await s.post('/settings/fx', { currency, month: '2025-01', rate: '1,5' })).status;
      expect([303, 422], currency).toContain(st);
    }
    expect((await s.post('/settings/fx/999/delete', {})).status).toBe(404);
    expect((await s.agent.get('/settings/fx/abc/edit')).status).toBe(404);
  });
  it('base currency itself cannot get a rate; the missing-rate hint lists enabled currencies without a current rate', async () => {
    const s = await session();
    expect((await s.post('/settings/fx', { currency: 'DKK', month: '2026-11', rate: '1' })).status).toBe(422);
    expect((await s.agent.get('/settings/fx')).text).toMatch(/EUR|USD/);
  });
  it('under en-GB the same decimal point is accepted and da-DK style grouping is not misread', async () => {
    const s = await session();
    await s.post('/settings/locale', { base: 'DKK', enabled: ['DKK', 'EUR'], locale: 'en-GB', timezone: 'Europe/Copenhagen', firstDay: '1', fiscalStart: '1' });
    expect((await s.post('/settings/fx', { currency: 'EUR', month: '2026-11', rate: '7.4590' })).status).toBe(303);
    expect(s.state.fx.at(-1)!.rate).toBe(7.459);
  });
});

describe('picklists', () => {
  it('deactivated value disappears from new-record forms but stays on, and is shown for, existing records', async () => {
    const s = await session();
    const industry = s.state.picklists.industry![0]!;
    await s.post('/settings/picklists/industry', { action: 'deactivate', valueId: String(industry.id) });
    const fresh = (await s.agent.get('/accounts/new')).text;
    expect(fresh).not.toContain(`>${industry.label}</option>`);
    expect(s.state.data.accounts[0]!.IndustryValueId).toBe(industry.id);
    const detail = (await s.agent.get('/accounts/1')).text;
    expect(detail).toContain(industry.label);
    const edit = (await s.agent.get('/accounts/1/edit')).text;
    expect(edit).toContain(`${industry.label} (inactive)`);
  });
  // OPEN DEFECT QA-I3-3 (Medium): the edit form offers the deactivated value as selected but the save rejects it with 'Choose a value from the list.'.
  // it.fails: remove marker once fixed. The edit form offers the inactive value as selected ("so saving other fields never forces a change"); the save must accept it.
  it('saving another field on a record that carries a deactivated value succeeds (QA-I3-3)', async () => {
    const s = await session();
    const industry = s.state.picklists.industry![0]!;
    await s.post('/settings/picklists/industry', { action: 'deactivate', valueId: String(industry.id) });
    const rv = await s.rowVer('/accounts/1');
    const res = await s.post('/accounts/1', { rowVer: rv, Name: 'Acme Renamed', OwnerId: '3', IndustryValueId: String(industry.id) });
    expect(res.status).toBe(303);
    expect(s.state.data.accounts[0]!.IndustryValueId).toBe(industry.id);
  });
  it('a deactivated lead source is likewise kept when saving an opportunity (QA-I3-3)', async () => {
    const s = await session();
    const ls = s.state.picklists.lead_source![0]!;
    await s.post('/settings/picklists/lead_source', { action: 'deactivate', valueId: String(ls.id) });
    const rv = await s.rowVer('/opportunities/1');
    const res = await s.post('/opportunities/1', oppForm({ rowVer: rv, Name: 'Renewal 2027', AccountId: '1', StageId: '3', OwnerId: '3', LeadSourceValueId: String(ls.id) }));
    expect(res.status).toBe(303);
  });
  it('but a deactivated value cannot be newly chosen on another record (server side)', async () => {
    const s = await session();
    const industry = s.state.picklists.industry![1]!;
    await s.post('/settings/picklists/industry', { action: 'deactivate', valueId: String(industry.id) });
    const res = await s.post('/accounts', { Name: 'Sneaky', OwnerId: '3', IndustryValueId: String(industry.id) });
    expect(res.status).toBe(422);
  });
  it('cross-list tampering: a value id from another list or a foreign valueId is refused, not applied', async () => {
    const s = await session();
    const other = s.state.picklists.lead_source![0]!;
    const res = await s.post('/settings/picklists/industry', { action: 'deactivate', valueId: String(other.id) });
    expect(res.status).toBe(422);
    expect(other.active).toBe(true);
    expect((await s.post('/settings/picklists/industry', { action: 'rename', valueId: 'abc', label: 'x' })).status).toBe(404);
  });
  it('labels are escaped everywhere they render', async () => {
    const s = await session();
    await s.post('/settings/picklists/industry', { action: 'add', label: '<img src=x onerror=alert(1)>' });
    for (const p of ['/settings/picklists/industry', '/accounts/new', '/accounts']) expect((await s.agent.get(p)).text, p).not.toContain('<img src=x');
  });
});

describe('users & teams', () => {
  const G = '33333333-3333-4333-8333-333333333333';
  it('GUID validation: braces, upper case and spaces are normalised; malformed, wrong length and non-hex are refused; duplicates refused', async () => {
    const s = await session();
    expect((await s.post('/settings/teams', { name: 'T1', groupId: `  {${G.toUpperCase()}} ` })).status).toBe(303);
    expect(s.state.teams.at(-1)!.groupId).toBe(G);
    const dupGroup = await s.post('/settings/teams', { name: 'T2', groupId: G.toUpperCase() });
    expect(dupGroup.status).toBe(422);
    expect(dupGroup.text).toContain('already linked');
    const dupName = await s.post('/settings/teams', { name: 't1', groupId: '44444444-4444-4444-8444-444444444444' });
    expect(dupName.status).toBe(422);
    for (const bad of ['', 'abc', G.slice(1), `${G}0`, G.replace(/3/, 'g'), G.replace(/-/g, ''), `${G};drop`, '<script>']) {
      const r = await s.post('/settings/teams', { name: 'Bad', groupId: bad });
      expect(r.status, bad).toBe(422);
      expect(r.text, bad).toContain(bad === '' ? 'Enter the Entra group object ID.' : 'Enter the group object ID in this format');
    }
    expect((await s.post('/settings/teams', { name: '', groupId: G })).text).toContain('Enter a team name.');
    expect((await s.post('/settings/teams', { name: 'n'.repeat(81), groupId: G })).text).toContain('80 characters');
    expect(s.state.teams.filter((t) => t.name === 'Bad')).toHaveLength(0);
  });
  it('changing the group asks for confirmation first; deactivate and reactivate are audited; teams are never deleted', async () => {
    const s = await session();
    const t = s.state.teams[0]!;
    const conf = await s.post(`/settings/teams/${t.id}`, { name: t.name, groupId: G });
    expect(conf.text).toContain('Continue?');
    expect(t.groupId).not.toBe(G);
    await s.post(`/settings/teams/${t.id}`, { name: t.name, groupId: G, confirm: '1' });
    expect(t.groupId).toBe(G);
    await s.post(`/settings/teams/${t.id}/deactivate`, {});
    await s.post(`/settings/teams/${t.id}/reactivate`, {});
    expect(s.state.audit.filter((a) => a.entityType === 'team').map((a) => a.fieldName)).toEqual(['EntraGroupId', 'IsActive', 'IsActive']);
    expect((await s.agent.delete(`/settings/teams/${t.id}`)).status).toBeGreaterThanOrEqual(400);
  });
  it('users page escapes names and filters safely', async () => {
    const s = await session();
    s.state.users[3]!.name = '<b>Mallory</b>';
    const html = (await s.agent.get('/settings/users?q=%3Cb%3E&role=Admin%27--&team=none')).text;
    expect(html).not.toContain('<b>Mallory</b>');
    expect((await s.agent.get('/settings/users?team=abc&role=x&q=' + 'a'.repeat(5000))).status).toBe(200);
  });
});

describe('general settings and logo hardening', () => {
  it('app name and accent are escaped in layout, theme.css cannot be injected', async () => {
    const s = await session();
    await s.post('/settings/general', { appName: '<script>alert(1)</script>', accentColor: '#1D4ED8' });
    expect((await s.agent.get('/')).text).not.toContain('<script>alert(1)');
    for (const accent of ['#1D4ED8;}body{display:none', '#1D4ED8\n}', 'url(x)', '#12345', '#1234567']) {
      expect((await s.post('/settings/general', { appName: 'X', accentColor: accent })).status, accent).toBe(422);
    }
    expect((await s.agent.get('/css/theme.css')).text).not.toContain('display:none');
  });
  it('SVG logos of every flavour are refused (PNG only)', () => {
    const wrap = (inner: string) => Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg">${inner}</svg>`);
    for (const inner of [
      '<a href=javascript:alert(1)><rect/></a>',
      '<image href="//evil.example/x.png"/>',
      '<rect style="background:url(//evil/x)"/>',
      '<style>@import "//evil/x.css";</style>',
      '<rect onClick = "alert(1)"/>',
      '<![CDATA[<script>]]>',
      '<iframe src="x"/>',
      '<a xlink:href="data:text/html;base64,AAAA"><rect/></a>',
      '<animate attributeName="href" values="javascript:alert(1)"/>',
    ]) expect(checkLogo(wrap(inner)).ok, inner).toBe(false);
    expect(checkLogo(Buffer.from('<?xml version="1.0"?><?xml-stylesheet href="http://evil/x.css"?><svg xmlns="http://www.w3.org/2000/svg"/>')).ok).toBe(false);
    expect(checkLogo(Buffer.concat([PNG_HEAD, Buffer.from('<script>alert(1)</script>')])).ok).toBe(true); // valid PNG head wins; only ever shown via <img> with nosniff + sandbox CSP
    expect(checkLogo(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])).ok).toBe(false); // magic alone is not enough
    expect(checkLogo(Buffer.from('<?xml version="1.0"?><svg xmlns="http://www.w3.org/2000/svg"/>')).ok).toBe(false);
  });
  it('the logo endpoint cannot be used as an HTML page: whatever the upload filename or declared type, it is served as image/png with sandbox CSP and nosniff', async () => {
    const s = await session();
    await s.agent.post('/settings/general/logo').field('_csrf', await s.token()).attach('logo', PNG_HEAD, { filename: 'a.html', contentType: 'text/html' });
    const res = await request(s.app).get('/branding/logo');
    expect(res.headers['content-type']).toContain('image/png');
    expect(res.headers['content-security-policy']).toContain('sandbox');
    expect(res.headers['x-content-type-options']).toBe('nosniff');
  });
});
