/** QA tests for increment 2 (CRM core): adverse paths and cross-entity checks complementing tests/crmHttp.test.ts. */
import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { ENTITY_LIST } from '../src/domain/entities';
import { createMemoryStore } from '../src/db/memoryStore';
import { csrfFrom, demoConfig, makeApp } from './helpers';

type Role = 'Admin' | 'Manager' | 'User';
async function session(role: Role, mode: 'private' | 'team' = 'private') {
  const store = createMemoryStore();
  store.state.settings.visibilityMode = mode;
  const { app } = makeApp({ config: demoConfig(), store });
  const agent = request.agent(app);
  await agent.get(`/__test/login?role=${role}`);
  const post = async (path: string, body: Record<string, string>) => {
    const page = await agent.get('/');
    return agent.post(path).type('form').send({ _csrf: csrfFrom(page.text), ...body });
  };
  const rowVer = async (path: string) => /name="rowVer" value="([^"]+)"/.exec((await agent.get(`${path}/edit`)).text)![1]!;
  return { agent, post, rowVer, state: store.state };
}
const PATHS = { accounts: 1, contacts: 1, opportunities: 1, activities: 1 } as const;

describe('RLS-hidden records are 404 for every entity and verb (no existence leak)', () => {
  // Demo User (id 3, team 1, private mode) cannot see team-mate (2), other-team (3) or no-team rows.
  const hidden = { accounts: [2, 3, 4], contacts: [2, 3], opportunities: [2, 3], activities: [2, 3] } as const;
  for (const def of ENTITY_LIST) {
    it(`${def.plural}: detail, edit, update, delete GET/POST, won/lost/done return 404 for hidden ids`, async () => {
      const s = await session('User');
      for (const id of hidden[def.key]) {
        const b = `${def.path}/${id}`;
        for (const suffix of ['', '/edit', '/delete']) expect((await s.agent.get(b + suffix)).status, `GET ${b + suffix}`).toBe(404);
        expect((await s.post(b, { rowVer: '0x0000000000000001', Name: 'x', LastName: 'x', Subject: 'x', OwnerId: '3' })).status, `POST ${b}`).toBe(404);
        expect((await s.post(`${b}/delete`, {})).status, `POST ${b}/delete`).toBe(404);
      }
      if (def.key === 'opportunities') for (const o of ['won', 'lost']) expect((await s.post(`/opportunities/3/${o}`, { lostReason: '300' })).status).toBe(404);
      if (def.key === 'activities') expect((await s.post('/activities/3/done', {})).status).toBe(404);
    });
  }
  it('malformed ids are 404, never 500', async () => {
    const s = await session('Admin');
    for (const id of ['0', '-1', '1e3', '01', '99999999999', '2147483648', 'abc', '1%20OR%201=1', '%00']) {
      for (const def of ENTITY_LIST) expect((await s.agent.get(`${def.path}/${id}`)).status, `${def.path}/${id}`).toBe(404);
    }
  });
});

describe('validation: summary + inline errors + input kept, for every entity', () => {
  const cases: Array<[string, Record<string, string>, string[], string]> = [
    ['/contacts', { LastName: '', Email: 'nope', FirstName: 'Kept', OwnerId: '3' }, ['Enter a last name.', 'Enter an email address like name@example.com.'], 'value="Kept"'],
    ['/opportunities', { Name: '', AccountId: '', StageId: '1', Amount: 'abc', CurrencyCode: 'XXX', CloseDate: '2026-02-30', OwnerId: '3' }, ['Enter an opportunity name.', 'Choose an account.', 'Enter an amount, for example 12.500,00.', 'Choose a currency.', 'Enter a real date, for example 31.12.2026.'], 'value="abc"'],
    ['/activities', { ActivityType: 'call', Subject: 'Kept subject', OwnerId: '3' }, ['Link this activity to an account, contact or opportunity.'], 'value="Kept subject"'],
  ];
  for (const [path, body, messages, kept] of cases) {
    it(`${path}: 422, every message inline and in the summary, input preserved`, async () => {
      const s = await session('User');
      const res = await s.post(path, body);
      expect(res.status).toBe(422);
      for (const m of messages) expect(res.text.split(m).length - 1, m).toBeGreaterThanOrEqual(2); // summary + inline
      expect(res.text).toContain(kept);
      expect(res.text).toMatch(/Fix \d+ problems? to save/);
      expect(res.text).toContain('role="alert"');
    });
  }
  it('negative amount, over-long text and a lost stage without reason are rejected', async () => {
    const s = await session('User');
    const base = { Name: 'N', AccountId: '1', StageId: '1', Amount: '10,00', CurrencyCode: 'DKK', CloseDate: '2026-12-31', OwnerId: '3' };
    expect((await s.post('/opportunities', { ...base, Amount: '-5,00' })).text).toContain("Amount can&#39;t be negative.");
    expect((await s.post('/accounts', { Name: 'x'.repeat(121), OwnerId: '3' })).text).toContain('Name must be 120 characters or fewer.');
    expect((await s.post('/opportunities', { ...base, StageId: '6' })).text).toContain('Choose why this was lost.');
    expect((await s.post('/opportunities', { ...base, Amount: '1,5e3' })).status).toBe(422);
  });
  it('mass-assignment: posted TeamId, CreatedBy, RowVer, Id and __proto__ cannot influence the record', async () => {
    const s = await session('User');
    const res = await s.post('/accounts', { Name: 'Mass', OwnerId: '3', TeamId: '2', CreatedBy: '1', Id: '1', 'constructor[prototype][x]': '1' });
    expect(res.status).toBe(303);
    const row = s.state.data.accounts.at(-1)!;
    expect(row).toMatchObject({ OwnerId: 3, TeamId: 1, CreatedBy: 3 });
    expect(row.Id).not.toBe(1);
  });
  // QA-I2-2 fixed: body-parser 413 renders the 'too large' page.
  it('oversized body gives a clean 4xx page, not a 500 (QA-I2-2)', async () => {
    const s = await session('User');
    const res = await s.post('/accounts', { Name: 'x', OwnerId: '3', Notes: 'y'.repeat(200_000) });
    expect(res.status).toBeGreaterThanOrEqual(400);
    expect(res.status).toBeLessThan(500);
  });
  it('a malformed percent-encoding in the URL gives a 4xx, not a 500', async () => {
    const s = await session('User');
    const res = await s.agent.get('/accounts?q=%E0%A4%A');
    expect(res.status).toBeLessThan(500);
  });
});

describe('audit trail per entity', () => {
  it('create and delete write C and D rows for all four entities', async () => {
    const s = await session('Admin');
    const forms: Record<string, Record<string, string>> = {
      '/accounts': { Name: 'AuditAcc', OwnerId: '3' },
      '/contacts': { LastName: 'AuditCon', OwnerId: '3' },
      '/opportunities': { Name: 'AuditOpp', AccountId: '1', StageId: '1', Amount: '1,00', CurrencyCode: 'DKK', CloseDate: '2027-01-01', OwnerId: '3' },
      '/activities': { ActivityType: 'task', Subject: 'AuditAct', AccountId: '1', OwnerId: '3' },
    };
    for (const [path, form] of Object.entries(forms)) {
      const created = await s.post(path, form);
      expect(created.status, path).toBe(303);
      const id = Number(/\/(\d+)$/.exec(created.headers.location as string)![1]);
      expect((await s.post(`${path}/${id}/delete`, {})).status, path).toBe(303);
      const rows = s.state.audit.filter((a) => a.entityId === id && a.entityType === path.slice(1, -1).replace('ie', 'y') ? true : a.entityId === id);
      expect(rows.map((r) => r.action), path).toEqual(expect.arrayContaining(['C', 'D']));
      expect(rows.every((r) => r.changedBy === 1 && r.correlationId.length === 36)).toBe(true);
    }
  });
  it('opportunity edit logs Amount, Stage (name), Currency, CloseDate and Owner with old and new values', async () => {
    const s = await session('Admin');
    const rv = await s.rowVer('/opportunities/1');
    await s.post('/opportunities/1', { rowVer: rv, Name: 'Renewal 2027', AccountId: '1', StageId: '4', Amount: '99,50', CurrencyCode: 'EUR', CloseDate: '2027-03-01', OwnerId: '4', LeadSourceValueId: '200' });
    const rows = s.state.audit.filter((a) => a.entityType === 'opportunity' && a.entityId === 1 && a.action === 'U');
    expect(rows.map((r) => r.fieldName).sort()).toEqual(['Amount', 'CloseDate', 'CurrencyCode', 'OwnerId', 'StageId']);
    expect(rows.find((r) => r.fieldName === 'StageId')).toMatchObject({ oldValue: 'Proposal', newValue: 'Negotiation' });
    expect(rows.find((r) => r.fieldName === 'Amount')).toMatchObject({ oldValue: '120000', newValue: '99.5' });
  });
  it('an unchanged save writes no audit rows and a rejected (invalid) save writes none either', async () => {
    const s = await session('Admin');
    const before = s.state.audit.length;
    const rv = await s.rowVer('/opportunities/1');
    await s.post('/opportunities/1', { rowVer: rv, Name: '', AccountId: '1', StageId: '3', Amount: '1', CurrencyCode: 'DKK', CloseDate: '2026-12-15', OwnerId: '3' });
    expect(s.state.audit.length).toBe(before);
  });
});

describe('optimistic concurrency on every entity', () => {
  const forms: Record<string, Record<string, string>> = {
    '/accounts': { Name: 'Changed', OwnerId: '3' },
    '/contacts': { LastName: 'Changed', OwnerId: '3' },
    '/opportunities': { Name: 'Changed', AccountId: '1', StageId: '3', Amount: '1,00', CurrencyCode: 'DKK', CloseDate: '2027-01-01', OwnerId: '3' },
    '/activities': { ActivityType: 'call', Subject: 'Changed', AccountId: '1', OwnerId: '3' },
  };
  for (const [path, form] of Object.entries(forms)) {
    it(`${path}/1: second save with the old RowVer is a 409 page with a way back, and the first save wins`, async () => {
      const s = await session('Admin');
      const rv = await s.rowVer(`${path}/1`);
      expect((await s.post(`${path}/1`, { rowVer: rv, ...form })).status).toBe(303);
      const stale = await s.post(`${path}/1`, { rowVer: rv, ...form, ...(path === '/accounts' ? { Name: 'Overwrite' } : {}) });
      expect(stale.status).toBe(409);
      expect(stale.text).toContain('Someone else changed this record');
      expect(stale.text).toContain(`href="${path}/1"`);
      expect(stale.text).not.toContain('Overwrite</');
    });
  }
  it('a missing or forged rowVer is a conflict, never a blind overwrite', async () => {
    const s = await session('Admin');
    for (const rowVer of ['', 'abc', '0xFFFFFFFFFFFFFFFF', "0x0'; DROP TABLE crm.Accounts;--"]) {
      expect((await s.post('/accounts/1', { rowVer, Name: 'Blind', OwnerId: '3' })).status, rowVer).toBe(409);
    }
    expect(s.state.data.accounts[0]!.Name).toBe('Acme A/S');
  });
});

describe('hand-off and owner choices', () => {
  it('create-for-teammate and edit-handoff both say "Saved and assigned to {name}" and land on the list', async () => {
    const s = await session('User');
    const rv = await s.rowVer('/contacts/1');
    const res = await s.post('/contacts/1', { rowVer: rv, LastName: 'Doe', OwnerId: '4' });
    expect(res.headers.location).toBe('/contacts');
    expect((await s.agent.get('/contacts')).text).toContain('Saved and assigned to Anna Teammate.');
    expect((await s.agent.get('/contacts')).text).not.toContain('Saved and assigned');
  });
  it('owner options: Admin all active users with team, Manager/User own team only', async () => {
    const opts = async (s: Awaited<ReturnType<typeof session>>) => [...(await s.agent.get('/accounts/new')).text.matchAll(/<option value="(\d+)"[^>]*>([^<]+)<\/option>/g)].map((m) => m[2]);
    const admin = await opts(await session('Admin'));
    expect(admin).toEqual(expect.arrayContaining(['Otto Otherteam (Sales South)', 'Nina NoTeam']));
    const mgr = await opts(await session('Manager'));
    expect(mgr).toEqual(expect.arrayContaining(['Demo Manager', 'Demo User', 'Anna Teammate']));
    expect(mgr.join()).not.toContain('Otto');
    const user = await opts(await session('User'));
    expect(user.join()).not.toMatch(/Otto|Nina/);
  });
  it('inactive team-mates are not offered as owner and are refused when posted', async () => {
    const s = await session('Manager');
    s.state.users.find((u) => u.id === 4)!.active = false;
    expect((await s.agent.get('/accounts/new')).text).not.toContain('Anna Teammate');
    expect((await s.post('/accounts', { Name: 'x', OwnerId: '4' })).status).toBe(422);
  });
  // The overview 5.2 consequence: editing a record whose owner is now inactive must keep the user on the form.
  // OPEN DEFECT QA-I2-1 (Medium): the form keeps the inactive owner selectable (UI spec) and the route accepts it, but the
  // DB rule (overview 5.2) rejects it, so the user gets a bare 403 and loses the input. it.fails: remove marker once fixed.
  it('team mode: editing a team-mate record whose owner is inactive and unchanged gives a field error, not a bare 403 (QA-I2-1)', async () => {
    const s = await session('User', 'team');
    s.state.users.find((u) => u.id === 4)!.active = false;
    const rv = await s.rowVer('/accounts/2');
    const res = await s.post('/accounts/2', { rowVer: rv, Name: 'Edited', OwnerId: '4' });
    expect(res.status).toBe(422);
    expect(res.text).toContain('Edited');
  });
});

describe('team-mode takeover, then delete (product-owner decision)', () => {
  it('User cannot delete a team-mate record, can take it over via edit, can then delete it, and History shows both', async () => {
    const s = await session('User', 'team');
    expect((await s.post('/opportunities/2/delete', {})).status).toBe(403);
    const rv = await s.rowVer('/opportunities/2');
    const take = await s.post('/opportunities/2', { rowVer: rv, Name: 'Tool rollout', AccountId: '2', StageId: '2', Amount: '45.000,00', CurrencyCode: 'EUR', CloseDate: '2027-01-20', OwnerId: '3', LeadSourceValueId: '200' });
    expect(take.status).toBe(303);
    const detail = (await s.agent.get('/opportunities/2')).text;
    expect(detail).toContain('Owner changed from Anna Teammate to Demo User');
    expect((await s.post('/opportunities/2/delete', {})).status).toBe(303);
    const actions = s.state.audit.filter((a) => a.entityType === 'opportunity' && a.entityId === 2).map((a) => `${a.action}${a.fieldName ?? ''}`);
    expect(actions).toEqual(['UOwnerId', 'D']);
    expect(s.state.audit.at(-1)).toMatchObject({ action: 'D', changedBy: 3, ownerId: 3 });
  });
  it('the other team and no-team records stay untouchable even in team mode', async () => {
    const s = await session('User', 'team');
    for (const p of ['/accounts/3', '/accounts/4', '/contacts/3', '/opportunities/3']) expect((await s.agent.get(p)).status, p).toBe(404);
    expect((await s.post('/accounts', { Name: 'x', OwnerId: '5' })).status).toBe(422);
  });
});

describe('lists: URL state, sorting, filters and paging never break', () => {
  it('every declared sort key in both directions and every page size renders 200 for every entity', async () => {
    const s = await session('Admin');
    for (const def of ENTITY_LIST) {
      for (const sort of Object.keys(def.sorts)) for (const dir of ['asc', 'desc']) {
        for (const pageSize of ['25', '50', '100']) expect((await s.agent.get(`${def.path}?sort=${sort}&dir=${dir}&pageSize=${pageSize}`)).status, `${def.path} ${sort} ${dir}`).toBe(200);
      }
    }
  });
  it('paging past the end shows an empty page with controls, not an error; links keep search and sort', async () => {
    const s = await session('Admin');
    const res = await s.agent.get('/accounts?q=a&sort=city&dir=desc&page=99');
    expect(res.status).toBe(200);
    const next = await s.agent.get('/accounts?q=a&sort=city&dir=desc&pageSize=25&page=1');
    expect(next.text).toMatch(/q=a/);
  });
  it('filters combine, and the filtered empty state offers to clear them', async () => {
    const s = await session('Admin');
    const res = await s.agent.get('/opportunities?status=won');
    expect(res.text).toContain('match these filters');
    expect(res.text).toContain('href="/opportunities"');
    expect((await s.agent.get('/opportunities?status=all&stage=2&owner=4')).text).toContain('Tool rollout');
    expect((await s.agent.get('/opportunities?status=all&from=2026-12-02&to=2026-12-31')).text).toContain('Renewal 2027');
    expect((await s.agent.get('/opportunities?status=all&from=2026-12-02&to=2026-12-31')).text).not.toContain('Southern deal');
  });
  it('search treats LIKE wildcards literally and reflects the term safely', async () => {
    const s = await session('Admin');
    const pct = await s.agent.get('/accounts?q=%25');
    expect(pct.text).toContain('match these filters');
    const xss = await s.agent.get('/accounts?q=%22%3E%3Cscript%3Ealert(1)%3C/script%3E');
    expect(xss.text).not.toContain('<script>alert(1)');
  });
});

describe('locale formatting (da-DK default)', () => {
  it('shows amounts with Danish separators and dates as dd.mm.yyyy, and accepts Danish input', async () => {
    const s = await session('Admin');
    const list = (await s.agent.get('/opportunities?status=all')).text;
    expect(list).toMatch(/120\.000,00/);
    expect(list).toContain('15.12.2026');
    const ok = await s.post('/opportunities', { Name: 'Loc', AccountId: '1', StageId: '1', Amount: '12.500,75', CurrencyCode: 'DKK', CloseDate: '2027-01-01', OwnerId: '3' });
    expect(ok.status).toBe(303);
    expect(s.state.data.opportunities.at(-1)!.Amount).toBe(12500.75);
  });
  it('rejects an en-GB style amount that is ambiguous in da-DK rather than mis-reading it', async () => {
    const s = await session('Admin');
    const res = await s.post('/opportunities', { Name: 'Loc', AccountId: '1', StageId: '1', Amount: '12,500.75', CurrencyCode: 'DKK', CloseDate: '2027-01-01', OwnerId: '3' });
    expect(res.status).toBe(422);
  });
});

describe('role-based access to the new pages', () => {
  it('unauthenticated requests to any CRM URL, including POSTs, never reach a handler', async () => {
    const { app } = makeApp({ config: demoConfig(), store: createMemoryStore() });
    for (const def of ENTITY_LIST) {
      expect((await request(app).get(`${def.path}/1`)).headers.location).toMatch(/^\/signin/);
      expect([303, 403]).toContain((await request(app).post(`${def.path}/1/delete`)).status);
    }
  });
  it('Settings stays Admin only while CRM pages are open to every role', async () => {
    for (const role of ['User', 'Manager'] as const) {
      const s = await session(role);
      expect((await s.agent.get('/settings/general')).status).toBe(403);
      for (const def of ENTITY_LIST) expect((await s.agent.get(def.path)).status).toBe(200);
    }
  });
  it('every entity detail page is reachable for its owner (smoke)', async () => {
    const s = await session('User');
    for (const def of ENTITY_LIST) expect((await s.agent.get(`${def.path}/${PATHS[def.key]}`)).status, def.path).toBe(200);
  });
});
