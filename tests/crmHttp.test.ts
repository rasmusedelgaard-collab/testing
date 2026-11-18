import request from 'supertest';
import { describe, expect, it } from 'vitest';
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
  return { agent, store, post, state: store.state };
}
const names = (html: string) => [...html.matchAll(/<a href="\/(?:accounts|contacts|opportunities|activities)\/\d+">([^<]+)<\/a>/g)].map((m) => m[1]);
const accountForm = (over: Record<string, string> = {}) => ({ Name: 'New Co', OwnerId: '3', ...over });

describe('lists: RLS-shaped visibility, search, filters, sorting, paging', () => {
  it('private mode: a User sees only own accounts; team mode adds team-mates; Manager sees the team; Admin all', async () => {
    expect(names((await (await session('User')).agent.get('/accounts')).text)).toEqual(['Acme A/S']);
    expect(names((await (await session('User', 'team')).agent.get('/accounts')).text).sort()).toEqual(['Acme A/S', 'Manager Metals', 'Nordic Tools ApS']);
    expect(names((await (await session('Manager')).agent.get('/accounts')).text).sort()).toEqual(['Acme A/S', 'Manager Metals', 'Nordic Tools ApS']);
    expect((await (await session('Admin')).agent.get('/accounts')).text).toContain('Southern Foods');
  });
  it('search, filter and sort live in the URL', async () => {
    const { agent } = await session('Admin');
    expect(names((await agent.get('/accounts?q=nordic')).text)).toEqual(['Nordic Tools ApS']);
    expect(names((await agent.get('/accounts?sort=name&dir=desc')).text)[0]).toBe('Southern Foods');
    expect(names((await agent.get('/accounts?owner=4')).text)).toEqual(['Nordic Tools ApS']);
    const res = await agent.get('/accounts?sort=name&dir=asc');
    expect(res.text).toContain('aria-sort="ascending"');
    expect(res.text).toContain('href="/accounts?sort=name&amp;dir=desc&amp;pageSize=25&amp;page=1"');
  });
  it('ignores unknown sort keys and junk filters instead of failing or injecting', async () => {
    const { agent } = await session('Admin');
    expect((await agent.get("/accounts?sort=Name;DROP TABLE x&dir=sideways&owner=abc&page=-4&pageSize=9999")).status).toBe(200);
  });
  it('paginates at 25 by default and honours pageSize', async () => {
    const { agent, state } = await session('Admin');
    for (let i = 0; i < 30; i++) state.data.accounts.push({ ...state.data.accounts[0]!, Id: 100 + i, Name: `Bulk ${String(i).padStart(2, '0')}` });
    const p1 = await agent.get('/accounts?q=Bulk');
    expect(names(p1.text)).toHaveLength(25);
    expect(p1.text).toContain('Showing 1–25 of 30');
    expect(names((await agent.get('/accounts?q=Bulk&page=2')).text)).toHaveLength(5);
    expect(names((await agent.get('/accounts?q=Bulk&pageSize=50')).text)).toHaveLength(30);
  });
  it('shows both empty states', async () => {
    const { agent } = await session('User');
    expect((await agent.get('/accounts?q=zzzz')).text).toContain('match these filters');
    const s = await session('User');
    s.state.data.contacts.length = 0;
    expect((await s.agent.get('/contacts')).text).toContain('No contacts yet.');
  });
  it('opportunities default to Open, amounts and dates use the Settings locale (da-DK), and past close dates are marked', async () => {
    const { agent, state } = await session('Admin');
    state.data.opportunities[0]!.CloseDate = '2020-01-31';
    const html = (await agent.get('/opportunities')).text;
    expect(html).toContain('DKK 120.000,00');
    expect(html).toContain('31.01.2020');
    expect(html).toContain('Past close date');
    state.settings.localeCode = 'en-GB';
    expect((await agent.get('/opportunities')).text).toContain('DKK 120,000.00');
  });
  it('activities default to Open and the done checkbox is a POST form with a descriptive name', async () => {
    const { agent } = await session('Admin');
    const html = (await agent.get('/activities')).text;
    expect(html).toContain('☐ Mark done<span class="visually-hidden">: Call Acme</span>');
    expect(html).not.toContain('aria-label="Mark');
  });
});

describe('create / edit', () => {
  it('creates an account, redirects to it with a flash, and writes a create audit row', async () => {
    const { agent, post, state } = await session('User');
    const res = await post('/accounts', accountForm());
    expect(res.status).toBe(303);
    const detail = await agent.get(res.headers.location as string);
    expect(detail.text).toContain('Account saved.');
    expect(detail.text).toContain('New Co');
    expect(state.audit.at(-1)).toMatchObject({ action: 'C', entityType: 'account', newValue: 'New Co', changedBy: 3 });
  });
  it('derives TeamId from the owner row and ignores a posted TeamId', async () => {
    const { post, state } = await session('Admin');
    await post('/accounts', accountForm({ OwnerId: '5', TeamId: '1' }));
    expect(state.data.accounts.at(-1)).toMatchObject({ OwnerId: 5, TeamId: 2 });
  });
  it('shows an error summary and inline errors with the exact copy, keeps input, and returns 422', async () => {
    const { post } = await session('User');
    const res = await post('/accounts', { Name: '', Website: 'not a site', OwnerId: '3', City: 'Kept City' });
    expect(res.status).toBe(422);
    expect(res.text).toContain('Fix 2 problems to save');
    expect(res.text).toContain('Enter an account name.');
    expect(res.text).toContain('Enter a web address like example.com.');
    expect(res.text).toContain('value="Kept City"');
    expect(res.text).toContain('aria-invalid="true"');
    expect(res.text).toContain('role="alert"');
  });
  it('requires a CSRF token', async () => {
    const { agent } = await session('User');
    expect((await agent.post('/accounts').type('form').send(accountForm())).status).toBe(403);
  });
  it('Users may only choose themselves or active team-mates as owner; Admin may choose anyone', async () => {
    const u = await session('User');
    const bad = await u.post('/accounts', accountForm({ OwnerId: '5' }));
    expect(bad.status).toBe(422);
    expect(bad.text).toContain('Choose an owner from your team.');
    expect((await u.post('/accounts', accountForm({ OwnerId: '4' }))).status).toBe(303);
    u.state.users.find((x) => x.id === 4)!.active = false;
    expect((await u.post('/accounts', accountForm({ OwnerId: '4' }))).status).toBe(422);
    expect((await (await session('Admin')).post('/accounts', accountForm({ OwnerId: '5' }))).status).toBe(303);
  });
  it('owner dropdown lists only team-mates for a User (no other-team users)', async () => {
    const { agent } = await session('User');
    const html = (await agent.get('/accounts/new')).text;
    expect(html).toContain('Anna Teammate');
    expect(html).not.toContain('Otto Otherteam');
    expect(html).toContain('You can choose people in your team.');
    const admin = (await (await session('Admin')).agent.get('/accounts/new')).text;
    expect(admin).toContain('Otto Otherteam (Sales South)');
  });
  it('hand-off: assigning a record to someone you then cannot see redirects to the list with "Saved and assigned to"', async () => {
    const { agent, post } = await session('User');
    const res = await post('/accounts', accountForm({ OwnerId: '4' }));
    expect(res.headers.location).toBe('/accounts');
    expect((await agent.get('/accounts')).text).toContain('Saved and assigned to Anna Teammate.');
  });
  it('verifies referenced records are visible before writing', async () => {
    const { post, state } = await session('User');
    const before = state.data.contacts.length;
    const res = await post('/contacts', { LastName: 'Probe', AccountId: '3', OwnerId: '3' }); // account 3 belongs to another team
    expect(res.status).toBe(422);
    expect(res.text).toContain('Choose an account from the list.');
    expect(state.data.contacts).toHaveLength(before);
    expect((await post('/contacts', { LastName: 'Fine', AccountId: '1', OwnerId: '3' })).status).toBe(303);
  });
  it('edit saves changes, bumps RowVer, shows "Last modified by", and audits owner changes', async () => {
    const { agent, post, state } = await session('Manager');
    const form = (await agent.get('/accounts/1/edit')).text;
    const rowVer = /name="rowVer" value="([^"]+)"/.exec(form)![1]!;
    const res = await post('/accounts/1', { rowVer, Name: 'Acme Renamed', OwnerId: '4' });
    expect(res.status).toBe(303);
    const detail = (await agent.get('/accounts/1')).text;
    expect(detail).toContain('Acme Renamed');
    expect(detail).toContain('Last modified by Demo Manager');
    expect(state.audit.at(-1)).toMatchObject({ action: 'U', fieldName: 'OwnerId', oldValue: '3', newValue: '4', changedBy: 2 });
  });
  it('optimistic concurrency: a stale RowVer gives the 409 edit-conflict page and does not overwrite', async () => {
    const { agent, post, state } = await session('Manager');
    const rowVer = /name="rowVer" value="([^"]+)"/.exec((await agent.get('/accounts/1/edit')).text)![1]!;
    expect((await post('/accounts/1', { rowVer, Name: 'First', OwnerId: '3' })).status).toBe(303);
    const stale = await post('/accounts/1', { rowVer, Name: 'Second', OwnerId: '3' });
    expect(stale.status).toBe(409);
    expect(stale.text).toContain('Someone else changed this record');
    expect(state.data.accounts[0]!.Name).toBe('First');
  });
  it('editing a record that does not exist or is hidden gives 404', async () => {
    const { agent, post } = await session('User');
    expect((await agent.get('/accounts/999/edit')).status).toBe(404);
    expect((await agent.get('/accounts/3')).status).toBe(404); // other team
    expect((await agent.get('/accounts/2')).status).toBe(404); // team-mate, private mode
    expect((await post('/accounts/3', { rowVer: '0x0000000000000001', Name: 'x', OwnerId: '3' })).status).toBe(404);
    expect((await agent.get('/accounts/abc')).status).toBe(404);
    expect((await agent.get('/accounts/0')).status).toBe(404);
  });
  it('team mode: a User can edit a team-mate record', async () => {
    const { agent, post, state } = await session('User', 'team');
    const rowVer = /name="rowVer" value="([^"]+)"/.exec((await agent.get('/accounts/2/edit')).text)![1]!;
    expect((await post('/accounts/2', { rowVer, Name: 'Edited by mate', OwnerId: '4' })).status).toBe(303);
    expect(state.data.accounts[1]!.ModifiedBy).toBe(3);
  });
});

describe('opportunities', () => {
  const opp = (over: Record<string, string> = {}) => ({ Name: 'Big deal', AccountId: '1', StageId: '1', Amount: '12.500,00', CurrencyCode: 'EUR', CloseDate: '2027-02-01', OwnerId: '3', ...over });
  it('parses the locale amount, validates required fields, and rejects negatives', async () => {
    const { post, state } = await session('User');
    expect((await post('/opportunities', opp())).status).toBe(303);
    expect(state.data.opportunities.at(-1)).toMatchObject({ Amount: 12500, CurrencyCode: 'EUR', TeamId: 1 });
    expect((await post('/opportunities', opp({ Amount: '-5' }))).text).toContain("Amount can&#39;t be negative.");
    expect((await post('/opportunities', opp({ Amount: 'abc' }))).text).toContain('Enter an amount, for example 12.500,00.');
    expect((await post('/opportunities', opp({ CloseDate: '2027-02-30' }))).text).toContain('Enter a real date');
    expect((await post('/opportunities', opp({ CurrencyCode: 'XXX' }))).text).toContain('Choose a currency.');
  });
  it('a Lost stage requires a lost reason', async () => {
    const { post } = await session('User');
    const res = await post('/opportunities', opp({ StageId: '6' }));
    expect(res.status).toBe(422);
    expect(res.text).toContain('Choose why this was lost.');
    expect((await post('/opportunities', opp({ StageId: '6', LostReasonValueId: '300' }))).status).toBe(303);
  });
  it('audits Amount, Stage (by name), Currency and CloseDate changes', async () => {
    const { agent, post, state } = await session('User');
    const rowVer = /name="rowVer" value="([^"]+)"/.exec((await agent.get('/opportunities/1/edit')).text)![1]!;
    await post('/opportunities/1', { ...opp({ Name: 'Renewal 2027', StageId: '4', Amount: '130.000,00', CurrencyCode: 'DKK', CloseDate: '2026-12-20' }), rowVer });
    const fields = state.audit.filter((a) => a.action === 'U').map((a) => [a.fieldName, a.oldValue, a.newValue]);
    expect(fields).toEqual(expect.arrayContaining([['Amount', '120000', '130000'], ['StageId', 'Proposal', 'Negotiation'], ['CloseDate', '2026-12-15', '2026-12-20']]));
    expect(state.audit.every((a) => a.correlationId)).toBe(true);
  });
  const verOf = async (agent: ReturnType<typeof request.agent>, id: number) => /name="rowVer" value="([^"]+)"/.exec((await agent.get(`/opportunities/${id}`)).text)![1]!;
  it('Mark as won sets the won stage and today as close date when it was in the future; lost needs a reason', async () => {
    const { agent, post, state } = await session('User');
    const rv = await verOf(agent, 1);
    const ask = await post('/opportunities/1/won', { rowVer: rv });
    expect(ask.status).toBe(200);
    expect(ask.text).toContain('Mark Renewal 2027 as won? The stage changes to Closed won. The close date will be set to today.');
    expect(state.data.opportunities[0]!.StageId).toBe(3); // nothing changes until confirmed
    const res = await post('/opportunities/1/won', { rowVer: rv, confirm: '1' });
    expect(res.status).toBe(303);
    const row = state.data.opportunities[0]!;
    expect(row.StageId).toBe(5);
    expect(row.ClosedAt).toBeTruthy();
    expect((await agent.get('/opportunities/1')).text).toContain('marked as won');
    const s2 = await session('User');
    const noReason = await s2.post('/opportunities/1/lost', { rowVer: await verOf(s2.agent, 1), confirm: '1' });
    expect(noReason.status).toBe(422);
    expect(noReason.text).toContain('id="e-lostReason"');
    expect(noReason.text).toContain('aria-invalid="true" aria-describedby="e-lostReason"');
    expect(s2.state.data.opportunities[0]!.StageId).toBe(3);
    await s2.post('/opportunities/1/lost', { lostReason: '300', rowVer: await verOf(s2.agent, 1), confirm: '1' });
    expect(s2.state.data.opportunities[0]).toMatchObject({ StageId: 6, LostReasonValueId: 300 });
  });
  it('won/lost without a RowVer, or with a stale one, give the 409 conflict page and change nothing (N4)', async () => {
    const { agent, post, state } = await session('User');
    const stale = await verOf(agent, 1);
    expect((await post('/opportunities/1/won', {})).status).toBe(409);
    expect((await post('/opportunities/1/lost', { lostReason: '300' })).status).toBe(409);
    expect(state.data.opportunities[0]!.StageId).toBe(3);
    await post('/opportunities/1/won', { rowVer: stale, confirm: '1' });
    expect((await post('/opportunities/1/lost', { lostReason: '300', rowVer: stale, confirm: '1' })).status).toBe(409);
  });
});

describe('delete', () => {
  it('owner can delete: confirm page, then POST, audited with a D row', async () => {
    const { agent, post, state } = await session('User');
    expect((await agent.get('/activities/1/delete')).text).toContain('Delete');
    const res = await post('/activities/1/delete', {});
    expect(res.headers.location).toBe('/activities');
    expect(state.data.activities.some((a) => a.Id === 1)).toBe(false);
    expect(state.audit.at(-1)).toMatchObject({ action: 'D', entityType: 'activity', oldValue: 'Call Acme', changedBy: 3 });
  });
  it('team mode: a User sees no Delete button on a team-mate record, gets 403 on GET and POST, and the row survives', async () => {
    const { agent, post, state } = await session('User', 'team');
    expect((await agent.get('/accounts/2')).text).not.toContain('Delete account');
    expect((await agent.get('/accounts/1')).text).toContain('Delete account');
    expect((await agent.get('/accounts/2/delete')).status).toBe(403);
    expect((await post('/accounts/2/delete', {})).status).toBe(403);
    expect(state.data.accounts.some((a) => a.Id === 2)).toBe(true);
  });
  it('a Manager can delete a team-mate record; hidden/other-team rows are 404', async () => {
    const { post, state } = await session('Manager');
    expect((await post('/contacts/2/delete', {})).status).toBe(303);
    expect(state.data.contacts.some((c) => c.Id === 2)).toBe(false);
    expect((await post('/contacts/3/delete', {})).status).toBe(404);
  });
  it('unlinks contacts and activities when deleting an account, but refuses while opportunities still link to it (409 message)', async () => {
    const { post, state } = await session('Admin');
    const blocked = await post('/accounts/1/delete', {});
    expect(blocked.status).toBe(409);
    expect(blocked.text).toContain("This account still has linked opportunities or records you can&#39;t see.");
    expect(state.data.accounts.some((a) => a.Id === 1)).toBe(true);
    state.data.opportunities = state.data.opportunities.filter((o) => o.AccountId !== 1);
    expect((await post('/accounts/1/delete', {})).status).toBe(303);
    expect(state.data.contacts.find((c) => c.Id === 1)!.AccountId).toBeNull();
    expect(state.data.activities.find((a) => a.Id === 1)!.AccountId).toBeNull();
  });
  it('activities left with no links show "(deleted record)"', async () => {
    const { agent, post, state } = await session('Admin');
    state.data.opportunities = state.data.opportunities.filter((o) => o.AccountId !== 1);
    await post('/accounts/1/delete', {});
    expect((await agent.get('/activities')).text).toContain('(deleted record)');
  });
});

describe('activities', () => {
  const act = (over: Record<string, string> = {}) => ({ ActivityType: 'call', Subject: 'Ring', AccountId: '1', OwnerId: '3', ...over });
  it('requires at least one related record and drops due dates for notes', async () => {
    const { post, state } = await session('User');
    const none = await post('/activities', act({ AccountId: '' }));
    expect(none.status).toBe(422);
    expect(none.text).toContain('Link this activity to an account, contact or opportunity.');
    await post('/activities', act({ ActivityType: 'note', DueDate: '2026-12-01', DueTime: '10:00' }));
    expect(state.data.activities.at(-1)).toMatchObject({ ActivityType: 'note', DueDate: null, DueTime: null });
  });
  it('a time needs a date', async () => {
    const { post } = await session('User');
    expect((await post('/activities', act({ DueTime: '10:00' }))).text).toContain('Enter a due date for this time.');
  });
  it('mark done / not done, with flash, and the done activity leaves the default Open list', async () => {
    const { agent, post } = await session('User');
    const res = await post('/activities/1/done', { returnTo: '/activities' });
    expect(res.headers.location).toBe('/activities');
    const list = (await agent.get('/activities')).text;
    expect(list).toContain('Marked as done.');
    expect(list).not.toContain('Call Acme');
    expect((await agent.get('/activities?status=done')).text).toContain('Call Acme');
    await post('/activities/1/undone', {});
    expect((await agent.get('/activities')).text).toContain('Call Acme');
  });
  it('done on a hidden activity is 404; an unsafe returnTo is ignored', async () => {
    const { post } = await session('User');
    expect((await post('/activities/3/done', {})).status).toBe(404);
    expect((await post('/activities/1/done', { returnTo: '//evil.example' })).headers.location).toBe('/activities');
  });
  it('detail page shows related-to link and the timeline sections of an account', async () => {
    const { agent } = await session('Manager');
    const html = (await agent.get('/accounts/1?tab=activities')).text;
    expect(html).toContain('Activities (1)');
    expect(html).toContain('Call Acme');
    expect(html).toContain('Contacts (1)');
  });
});

describe('page security', () => {
  it('all CRM pages need sign-in', async () => {
    const { app } = makeApp({ config: demoConfig() });
    for (const p of ['/accounts', '/contacts/new', '/opportunities/1', '/activities/1/edit']) expect((await request(app).get(p)).status).toBe(303);
  });
  it('no inline scripts or styles in the new pages', async () => {
    const { agent } = await session('Admin');
    for (const p of ['/accounts', '/accounts/new', '/accounts/1', '/opportunities/1', '/activities/new', '/accounts/1/delete']) {
      const html = (await agent.get(p)).text;
      expect(html, p).not.toMatch(/<script(?![^>]*\ssrc=)/i);
      expect(html, p).not.toMatch(/\sstyle=/i);
    }
  });
  it('escapes user content', async () => {
    const { agent, post } = await session('User');
    const res = await post('/accounts', accountForm({ Name: '<img src=x onerror=alert(1)>' }));
    expect((await agent.get(res.headers.location as string)).text).not.toContain('<img src=x');
  });
});

describe('history timeline (owner changes are explicit)', () => {
  it('shows who changed the owner, from whom to whom, plus the takeover then delete flow in team mode', async () => {
    const { agent, post } = await session('User', 'team');
    const rowVer = /name="rowVer" value="([^"]+)"/.exec((await agent.get('/contacts/2/edit')).text)![1]!;
    expect((await post('/contacts/2', { rowVer, LastName: 'Hansen', OwnerId: '3' })).status).toBe(303);
    const html = (await agent.get('/contacts/2')).text;
    expect(html).toContain('History');
    expect(html).toContain('<strong>Owner changed from Anna Teammate to Demo User</strong>');
    expect(html).toContain('Demo User:');
    // now the owner, the User may delete; the delete is audited too
    expect(html).toContain('Delete contact');
    expect((await post('/contacts/2/delete', {})).status).toBe(303);
  });
  it('history is empty for an untouched record and logs a close-date change readably', async () => {
    const { agent, post } = await session('User');
    expect((await agent.get('/opportunities/1')).text).toContain('No changes recorded yet.');
    const rowVer = /name="rowVer" value="([^"]+)"/.exec((await agent.get('/opportunities/1/edit')).text)![1]!;
    await post('/opportunities/1', { rowVer, Name: 'Renewal 2027', AccountId: '1', StageId: '3', Amount: '120.000,00', CurrencyCode: 'DKK', CloseDate: '2027-01-05', OwnerId: '3' });
    expect((await agent.get('/opportunities/1')).text).toContain('Close date changed from 15.12.2026 to 05.01.2027');
  });
});

describe('UX review fixes (increment 2)', () => {
  it('H2: the list search field has its own id and label; the top-bar search keeps its own', async () => {
    const { agent } = await session('Admin');
    const html = (await agent.get('/accounts')).text;
    expect(html).toContain('<label for="f-q">Search</label><input id="f-q" name="q"');
    expect(html.match(/id="q"/g)).toHaveLength(1);
  });
  it('M9: the account menu name starts with the visible initials', async () => {
    const { agent } = await session('Admin');
    expect((await agent.get('/')).text).toContain('aria-label="DA – account menu for Demo Admin"');
  });
  it('H3: related-record Mark done buttons carry visible text plus hidden context', async () => {
    const { agent } = await session('Manager');
    const html = (await agent.get('/accounts/1?tab=activities')).text;
    expect(html).toContain('☐ Mark done<span class="visually-hidden">: Call Acme</span>');
  });
  it('M7: table cells carry data-label for the card layout', async () => {
    const { agent } = await session('Admin');
    const html = (await agent.get('/opportunities')).text;
    expect(html).toContain('<td data-label="Amount" class="num">');
  });
  it('H1/M1: the client script focuses the error summary, the first invalid field, or the flash', async () => {
    const { agent } = await session('Admin');
    const js = (await agent.get('/js/app.js')).text;
    expect(js).toContain("getElementById('error-summary')");
    expect(js).toContain('[aria-invalid="true"]');
    expect(js).toContain('main .flash');
  });
  it('L4: date-times read "18.11.2026 14:02" in da-DK', async () => {
    const { agent, state } = await session('Admin');
    state.data.accounts[0]!.ModifiedAt = '2026-11-18T13:02:00.000Z';
    state.data.accounts[0]!.ModifiedBy = 2;
    expect((await agent.get('/accounts/1')).text).toContain('Last modified by Demo Manager, 18.11.2026 14:02');
  });
  it('M4: won/lost need a confirmation page before anything changes', async () => {
    const { agent, post, state } = await session('User');
    const rv = /name="rowVer" value="([^"]+)"/.exec((await agent.get('/opportunities/1')).text)![1]!;
    const ask = await post('/opportunities/1/lost', { rowVer: rv, lostReason: '300' });
    expect(ask.text).toContain('Mark Renewal 2027 as lost? The stage changes to Closed lost (reason: Price).');
    expect(state.data.opportunities[0]!.StageId).toBe(3);
  });
});
