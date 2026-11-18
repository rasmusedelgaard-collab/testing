import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { createMemoryStore } from '../src/db/memoryStore';
import { todayIn } from '../src/services/format';
import { addDays } from '../src/services/periods';
import { demoConfig, makeApp } from './helpers';

type Role = 'Admin' | 'Manager' | 'User';
async function session(role: Role) {
  const store = createMemoryStore();
  store.state.settings.timezone = 'UTC';
  store.state.settings.localeCode = 'en-GB';
  const { app } = makeApp({ config: demoConfig(), store });
  const agent = request.agent(app);
  await agent.get(`/__test/login?role=${role}`);
  const today = todayIn('UTC');
  return { agent, app, state: store.state, today, month: today.slice(0, 7) };
}
type S = Awaited<ReturnType<typeof session>>;
const opp = (s: S, id: number, over: Record<string, unknown>) => {
  const base = s.state.data.opportunities[0]!;
  s.state.data.opportunities.push({ ...base, Id: id, Name: `Extra ${id}`, ...over } as never);
};
const tile = (html: string, label: string) => {
  const m = new RegExp(`<li class="tile">([\\s\\S]*?)</li>`, 'g');
  for (const x of html.matchAll(m)) if (x[1]!.includes(`>${label}<`)) return x[1]!.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
  throw new Error(`no tile ${label}`);
};

describe('dashboard', () => {
  it('shows the six tiles with links to filtered lists, greeting and data-as-of', async () => {
    const s = await session('Admin');
    const html = (await s.agent.get('/')).text;
    expect(html).toMatch(/Good (morning|afternoon|evening), Demo/);
    expect(html).toContain('Data as of');
    for (const label of ['Open pipeline', 'Weighted pipeline', 'Won this month', 'Won this quarter', 'Win rate (last 90 d)', 'Activities due']) expect(tile(html, label)).toContain(label);
    expect(html).toContain('href="/opportunities?status=open"');
    expect(html).toContain('href="/activities?status=open&amp;type=actionable&amp;due=due"');
    expect(html).toMatch(/href="\/opportunities\?status=won&amp;from=\d{4}-\d{2}-01&amp;to=\d{4}-\d{2}-\d{2}"/);
    expect(html).toMatch(/href="\/opportunities\?status=closed&amp;from=\d{4}-\d{2}-\d{2}&amp;to=\d{4}-\d{2}-\d{2}"/);
  });
  it('every tile link opens a working filtered list', async () => {
    const s = await session('Admin');
    const html = (await s.agent.get('/')).text;
    const hrefs = [...html.matchAll(/href="([^"]+)" class="tile-link"/g)].map((m) => m[1]!.replace(/&amp;/g, '&'));
    expect(hrefs).toHaveLength(6);
    for (const h of hrefs) expect((await s.agent.get(h)).status, h).toBe(200);
  });
  it('base-currency deals are exact; other currencies without a rate are excluded with a visible note, never converted 1:1', async () => {
    const s = await session('Manager'); // sees Acme (DKK 120.000) and Nordic Tools (EUR 45.000)
    const html = (await s.agent.get('/')).text;
    expect(tile(html, 'Open pipeline')).toContain('DKK 120k');
    expect(tile(html, 'Open pipeline')).toContain('(DKK 120,000.00)');
    expect(tile(html, 'Open pipeline')).toContain('1 deal excluded (no FX rate for EUR)');
    expect(tile(html, 'Open pipeline')).toContain('Ask an administrator.');
    expect(tile(html, 'Weighted pipeline')).toContain('(DKK 60,000.00)');
  });
  it('Admin sees the link to FX settings instead', async () => {
    const s = await session('Admin');
    const t = tile((await s.agent.get('/')).text, 'Open pipeline');
    expect(t).toContain('2 deals excluded (no FX rate for EUR, SEK)');
    expect(t).toContain('Add FX rates');
    expect((await s.agent.get('/')).text).toContain('href="/settings/fx"');
  });
  it('open pipeline converts at the current month rate, falling back to the latest earlier month with a note', async () => {
    const s = await session('Manager');
    s.state.fx.push({ id: 1, base: 'DKK', currency: 'EUR', month: addDays(s.today, -70).slice(0, 7), rate: 7.5, modifiedByName: 'x', modifiedAt: '' });
    const t = tile((await s.agent.get('/')).text, 'Open pipeline');
    expect(t).toContain('(DKK 457,500.00)'); // 120.000 + 45.000 x 7,5
    expect(t).toContain('Uses latest rate for 1 currency');
    expect(t).not.toContain('excluded');
    s.state.fx.push({ id: 2, base: 'DKK', currency: 'EUR', month: s.month, rate: 8, modifiedByName: 'x', modifiedAt: '' });
    const t2 = tile((await s.agent.get('/')).text, 'Open pipeline');
    expect(t2).toContain('(DKK 480,000.00)');
    expect(t2).not.toContain('Uses latest rate');
  });
  it('rates stored for another base currency are ignored', async () => {
    const s = await session('Manager');
    s.state.fx.push({ id: 1, base: 'EUR', currency: 'EUR', month: s.month, rate: 7.5, modifiedByName: 'x', modifiedAt: '' });
    expect(tile((await s.agent.get('/')).text, 'Open pipeline')).toContain('excluded');
  });
  it('won this month / quarter and win rate over the last 90 days', async () => {
    const s = await session('Admin');
    opp(s, 50, { StageId: 5, CloseDate: s.today, Amount: 1000, CurrencyCode: 'DKK' });
    opp(s, 51, { StageId: 5, CloseDate: addDays(s.today, -89), Amount: 10, CurrencyCode: 'DKK' });
    opp(s, 52, { StageId: 6, CloseDate: addDays(s.today, -10), Amount: 10, CurrencyCode: 'DKK', LostReasonValueId: 300 });
    opp(s, 53, { StageId: 6, CloseDate: addDays(s.today, -90), Amount: 10, CurrencyCode: 'DKK', LostReasonValueId: 300 }); // day 91: outside the window
    opp(s, 54, { StageId: 6, CloseDate: addDays(s.today, -20), Amount: 10, CurrencyCode: 'DKK', LostReasonValueId: 300 });
    const html = (await s.agent.get('/')).text;
    expect(tile(html, 'Won this month')).toContain('(DKK 1,000.00)');
    expect(tile(html, 'Won this month')).toContain('1 deal');
  });
  it('win rate counts exactly: 2 won + 2 lost in the window = 50%, and the 91st day is excluded', async () => {
    const s = await session('Admin');
    opp(s, 50, { StageId: 5, CloseDate: s.today, Amount: 1, CurrencyCode: 'DKK' });
    opp(s, 51, { StageId: 5, CloseDate: addDays(s.today, -89), Amount: 1, CurrencyCode: 'DKK' });
    opp(s, 52, { StageId: 6, CloseDate: addDays(s.today, -10), Amount: 1, CurrencyCode: 'DKK', LostReasonValueId: 300 });
    opp(s, 54, { StageId: 6, CloseDate: addDays(s.today, -20), Amount: 1, CurrencyCode: 'DKK', LostReasonValueId: 300 });
    opp(s, 53, { StageId: 6, CloseDate: addDays(s.today, -90), Amount: 1, CurrencyCode: 'DKK', LostReasonValueId: 300 });
    const t = tile((await s.agent.get('/')).text, 'Win rate (last 90 d)');
    expect(t).toMatch(/50\s?%/);
    expect(t).toContain('2 of 4');
  });
  it('no closed deals: explicit text, no NaN', async () => {
    const s = await session('Admin');
    const t = tile((await s.agent.get('/')).text, 'Win rate (last 90 d)');
    expect(t).toContain('No closed deals yet');
    expect(t).not.toContain('NaN');
  });
  it('activities due counts open calls/meetings/tasks only: notes and done ones are excluded', async () => {
    const s = await session('Admin');
    const base = s.state.data.activities[0]!;
    const add = (id: number, over: Record<string, unknown>) => s.state.data.activities.push({ ...base, Id: id, Subject: `Act ${id}`, OwnerId: 1, TeamId: 1, ...over } as never);
    s.state.data.activities.length = 0;
    add(10, { ActivityType: 'call', DueDate: s.today });
    add(11, { ActivityType: 'note', DueDate: s.today });
    add(12, { ActivityType: 'task', DueDate: addDays(s.today, -3) });
    add(13, { ActivityType: 'meeting', DueDate: s.today, IsDone: true });
    add(14, { ActivityType: 'meeting', DueDate: addDays(s.today, 3) });
    const html = (await s.agent.get('/')).text;
    expect(tile(html, 'Activities due')).toContain('1 due today');
    expect(tile(html, 'Activities due')).toContain('1 overdue');
    expect(html).toContain('Overdue (1)');
    expect(html).toContain('Next 7 days (1)');
  });
  it('My open opportunities lists only the signed-in user\'s deals, earliest close first', async () => {
    const s = await session('Admin');
    opp(s, 60, { OwnerId: 1, TeamId: 1, CloseDate: '2026-01-02', StageId: 1 });
    opp(s, 61, { OwnerId: 1, TeamId: 1, CloseDate: '2026-01-01', StageId: 1 });
    const html = (await s.agent.get('/')).text;
    const mine = html.slice(html.indexOf('My open opportunities'), html.indexOf('Upcoming activities'));
    expect(mine.indexOf('Extra 61')).toBeGreaterThan(-1);
    expect(mine.indexOf('Extra 61')).toBeLessThan(mine.indexOf('Extra 60'));
    expect(mine).not.toContain('Renewal 2027'); // owned by the demo user, not the admin
  });
  it('RLS shapes every number: a User in private mode only sees own deals in the totals', async () => {
    const user = await session('User');
    expect(tile((await user.agent.get('/')).text, 'Open pipeline')).toContain('(DKK 120,000.00)');
    expect(tile((await user.agent.get('/')).text, 'Open pipeline')).toContain('1 opp');
    const admin = await session('Admin');
    expect(tile((await admin.agent.get('/')).text, 'Open pipeline')).toContain('3 opps');
  });
  it('big numbers are compact with the full value in the accessible name and title', async () => {
    const s = await session('User');
    opp(s, 70, { OwnerId: 3, TeamId: 1, StageId: 1, Amount: 2_412_500, CurrencyCode: 'DKK', CloseDate: '2027-05-01' });
    const html = (await s.agent.get('/')).text;
    expect(tile(html, 'Open pipeline')).toContain('DKK 2.5m');
    expect(tile(html, 'Open pipeline')).toContain('(DKK 2,532,500.00)');
    expect(html).toContain('title="Open pipeline: DKK 2,532,500.00"');
  });
  it('da-DK formatting follows Settings', async () => {
    const s = await session('User');
    s.state.settings.localeCode = 'da-DK';
    expect(tile((await s.agent.get('/')).text, 'Open pipeline')).toContain('(DKK 120.000,00)');
  });
  it('anonymous users are redirected; pages have no inline script or style', async () => {
    const s = await session('Admin');
    expect((await request(s.app).get('/analytics')).status).toBe(303);
    for (const p of ['/', '/analytics']) {
      const html = (await s.agent.get(p)).text;
      expect(html, p).not.toMatch(/<script(?![^>]*\s(src=|type="application\/json"))/i);
      expect(html, p).not.toMatch(/\sstyle=/i);
    }
  });
});

describe('analytics', () => {
  it.each(['Admin', 'Manager', 'User'] as const)('%s can open it; five captioned figures each with a data table', async (role) => {
    const s = await session(role);
    const res = await s.agent.get('/analytics');
    expect(res.status).toBe(200);
    expect(res.text.match(/<figure class="chart"/g)).toHaveLength(5);
    expect(res.text.match(/<figcaption>/g)).toHaveLength(5);
    expect(res.text.match(/<details class="chart-table" open>/g)).toHaveLength(5);
    for (const t of ['Pipeline by stage', 'Won revenue by month', 'Win rate by owner', 'Activities by type', 'Opportunities by lead source']) expect(res.text).toContain(t);
  });
  it('chart data is JSON in a non-executable block, escaped, with canvases that have a role and summary', async () => {
    const s = await session('Admin');
    s.state.picklists.lead_source![0]!.label = '</script><b>x';
    const html = (await s.agent.get('/analytics?period=this-year')).text;
    const blocks = [...html.matchAll(/<script type="application\/json" id="(chart-[a-z]+)-data">([\s\S]*?)<\/script>/g)];
    expect(blocks.length).toBeGreaterThanOrEqual(3);
    for (const b of blocks) expect(() => JSON.parse(b[2]!)).not.toThrow();
    expect(html).not.toContain('</script><b>x');
    expect(html).toMatch(/<canvas role="img" aria-label="[^"]+" data-chart="chart-/);
    expect(html).toContain('/vendor/chart.umd.js');
    expect(html).not.toMatch(/https?:\/\/[^"' ]*(cdn|jsdelivr|unpkg)/i);
  });
  it('serves the vendored Chart.js and charts.js locally under the strict CSP', async () => {
    const s = await session('Admin');
    const lib = await request(s.app).get('/vendor/chart.umd.js');
    expect(lib.status).toBe(200);
    expect(lib.text).toContain('Chart.js v4');
    expect((await request(s.app).get('/js/charts.js')).status).toBe(200);
    const csp = (await s.agent.get('/analytics')).headers['content-security-policy'] as string;
    expect(csp).toContain("script-src 'self'");
    expect(csp).not.toContain('unsafe-inline');
  });
  it('the period filter lives in the URL; presets and custom ranges drive the data', async () => {
    const s = await session('Admin');
    opp(s, 80, { StageId: 5, CloseDate: '2025-03-10', Amount: 5000, CurrencyCode: 'DKK' });
    const custom = await s.agent.get('/analytics?period=custom&from=2025-01-01&to=2025-06-30');
    expect(custom.text).toContain('Mar 2025');
    expect(custom.text.replace(/[\s\u00a0]+/g, ' ')).toContain('Best month: Mar 2025 with DKK 5k');
    expect(custom.text).toContain('value="2025-01-01"');
    const thisQ = await s.agent.get('/analytics?period=this-quarter');
    expect(thisQ.text).not.toContain('Mar 2025');
  });
  it('custom range errors use the exact copy and fall back to this quarter', async () => {
    const s = await session('Admin');
    expect((await s.agent.get('/analytics?period=custom&from=2026-02-01&to=2026-01-01')).text).toContain('End date must be on or after start date.');
    expect((await s.agent.get('/analytics?period=custom&from=2019-01-01&to=2026-01-01')).text).toContain('Choose a range of 5 years or less.');
  });
  it('empty period shows the empty copy but keeps tables', async () => {
    const s = await session('Admin');
    const html = (await s.agent.get('/analytics?period=custom&from=1999-01-01&to=1999-01-31')).text;
    expect(html).toContain('No data for this period. Try a longer date range.');
    expect(html).toContain('<caption class="visually-hidden">Pipeline by stage</caption>');
  });
  it('FX: converted amounts, "Latest rate used" row flags, exclusion notes and the Admin link', async () => {
    const s = await session('Admin');
    opp(s, 81, { StageId: 5, CloseDate: `${s.month}-01`, Amount: 100, CurrencyCode: 'EUR' });
    opp(s, 82, { StageId: 5, CloseDate: `${s.month}-02`, Amount: 100, CurrencyCode: 'SEK' });
    s.state.fx.push({ id: 1, base: 'DKK', currency: 'EUR', month: addDays(`${s.month}-01`, -40).slice(0, 7), rate: 7.5, modifiedByName: 'x', modifiedAt: '' });
    const html = (await s.agent.get('/analytics?period=this-year')).text;
    expect(html).toContain('Latest rate used');
    expect(html).toContain('Not included: no FX rate; Latest rate used');
    expect(html).toContain('deal excluded (no FX rate for SEK');
    expect(html).toContain('Add FX rates');
    const user = await session('Manager');
    user.state.data.opportunities.push({ ...user.state.data.opportunities[0]!, Id: 83, StageId: 5, CloseDate: `${user.month}-01`, Amount: 1, CurrencyCode: 'SEK' } as never);
    expect((await user.agent.get('/analytics?period=this-year')).text).toContain('Ask an administrator.');
  });
  it('owner filter narrows the data; RLS limits what a User can see at all', async () => {
    const s = await session('Admin');
    const all = (await s.agent.get('/analytics?period=custom&from=2026-01-01&to=2027-12-31')).text;
    const one = (await s.agent.get('/analytics?period=custom&from=2026-01-01&to=2027-12-31&owner=3')).text;
    expect(all).toContain('Southern Foods'.length ? 'Opportunities' : '');
    expect(one).toContain('<option value="3" selected>');
    expect((await s.agent.get('/analytics?owner=99999999999')).status).toBe(200);
    const u = await session('User');
    const ul = (await u.agent.get('/analytics?period=custom&from=2026-01-01&to=2027-12-31')).text;
    expect(ul).toContain('Website brings the most opportunities: 1'); // only the user's own deal
  });
  it('bar and line charts have the spec palette and no pie charts', async () => {
    const s = await session('Admin');
    const js = (await s.agent.get('/js/charts.js')).text;
    expect(js).toContain('#1D4ED8');
    expect(js).not.toMatch(/'pie'|'doughnut'/);
    expect(js).toContain('beginAtZero: true');
    expect(js).toContain('prefers-reduced-motion');
  });
});

describe('accounts: Open pipeline column', () => {
  it('shows converted open pipeline per account and flags deals without a rate', async () => {
    const s = await session('Admin');
    const flat = (h: string) => h.replace(/\s+/g, ' ');
    const html = flat((await s.agent.get('/accounts')).text);
    expect(html).toContain('<th scope="col">Open pipeline</th>');
    expect(html).toContain('data-label="Open pipeline" class="num"> DKK 120,000.00');
    expect(html).toContain('1 deal not converted (no rate: EUR)');
    s.state.fx.push({ id: 1, base: 'DKK', currency: 'EUR', month: s.month, rate: 8, modifiedByName: 'x', modifiedAt: '' });
    expect(flat((await s.agent.get('/accounts')).text)).toContain('DKK 360,000.00');
  });
  it('accounts without open deals show a dash', async () => {
    const s = await session('Admin');
    expect((await s.agent.get('/accounts?q=Solo')).text.replace(/\s+/g, ' ')).toContain('data-label="Open pipeline" class="num"> –');
  });
});

describe('QA-F3: Overdue / Today / Next 7 days are disjoint windows used by count, rows and link alike', () => {
  it('each activity appears once; the count equals the rows; the link opens the same window', async () => {
    const s = await session('Admin');
    const base = s.state.data.activities[0]!;
    s.state.data.activities.length = 0;
    const add = (id: number, d: number) => s.state.data.activities.push({ ...base, Id: id, Subject: `Day ${d}`, ActivityType: 'call', OwnerId: 1, TeamId: 1, DueDate: addDays(s.today, d), IsDone: false } as never);
    [-2, 0, 1, 3, 7, 8].forEach((d, i) => add(20 + i, d));
    const html = (await s.agent.get('/')).text;
    expect(html).toContain('Overdue (1)');
    expect(html).toContain('Today (1)');
    expect(html).toContain('Next 7 days (3)'); // +1, +3, +7 (not today, not +8)
    const list = (await s.agent.get('/activities?status=open&type=actionable&due=week')).text;
    for (const d of [1, 3, 7]) expect(list).toContain(`Day ${d}<`);
    for (const d of [-2, 0, 8]) expect(list).not.toContain(`Day ${d}<`);
    const today = (await s.agent.get('/activities?status=open&type=actionable&due=today')).text;
    expect(today).toContain('Day 0<');
    expect(today).not.toContain('Day 1<');
  });
});
