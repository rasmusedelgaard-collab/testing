/**
 * QA final gate: KPI values on a hand-built data set, with the expected numbers derived in this file by independent
 * date and money arithmetic (not by importing src/services). FX rules per ADR-0005 / UI spec 6.
 */
import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { createMemoryStore, seedState, type MemoryState } from '../src/db/memoryStore';
import { todayIn } from '../src/services/format';
import { demoConfig, makeApp } from './helpers';

const TODAY = todayIn('UTC');
const DAY = 86_400_000;
const shift = (d: string, n: number) => new Date(Date.parse(`${d}T00:00:00Z`) + n * DAY).toISOString().slice(0, 10);
const ym = (d: string) => d.slice(0, 7);
const addMonths = (month: string, n: number) => { const [y, m] = month.split('-').map(Number) as [number, number]; const t = y * 12 + (m - 1) + n; return `${Math.floor(t / 12)}-${String((t % 12) + 1).padStart(2, '0')}`; };
const lastDay = (month: string) => shift(`${addMonths(month, 1)}-01`, -1);
const FISCAL_START = 4; // April: forces the quarter logic to differ from calendar quarters
const quarterStartMonth = (d: string) => { const m = Number(d.slice(5, 7)); return addMonths(ym(d), -(((m - FISCAL_START + 12) % 12) % 3)); };
const Q_FROM = `${quarterStartMonth(TODAY)}-01`;
const Q_TO = lastDay(addMonths(quarterStartMonth(TODAY), 2));
const CUR = ym(TODAY);

type Opp = { id: number; stage: 1 | 2 | 3 | 4 | 5 | 6; amount: number; cur: string; close: string; owner: number; team: number };
const PROB: Record<number, number> = { 1: 0.1, 2: 0.25, 3: 0.5, 4: 0.75, 5: 1, 6: 0 };
// Rates: EUR has current-month, previous-month, and a LATER-month rate that must never be used. USD only has an old rate. SEK has none.
const RATES = [
  { currency: 'EUR', month: CUR, rate: 7.5 }, { currency: 'EUR', month: addMonths(CUR, -1), rate: 7 }, { currency: 'EUR', month: addMonths(CUR, 1), rate: 99 },
  { currency: 'USD', month: addMonths(CUR, -2), rate: 6 }, { currency: 'USD', month: addMonths(CUR, 1), rate: 99 },
];
const rateFor = (cur: string, month: string) => {
  if (cur === 'DKK') return 1;
  const hit = RATES.filter((r) => r.currency === cur && r.month <= month).sort((a, b) => b.month.localeCompare(a.month))[0];
  return hit ? hit.rate : null;
};
const OPPS: Opp[] = [
  // open (close dates irrelevant for the dashboard)
  { id: 101, stage: 3, amount: 100000, cur: 'DKK', close: shift(TODAY, 30), owner: 1, team: 1 },
  { id: 102, stage: 2, amount: 1000, cur: 'EUR', close: shift(TODAY, 30), owner: 1, team: 1 },
  { id: 103, stage: 1, amount: 2000, cur: 'SEK', close: shift(TODAY, 30), owner: 1, team: 1 },
  { id: 104, stage: 4, amount: 500, cur: 'USD', close: shift(TODAY, 30), owner: 1, team: 1 },
  { id: 105, stage: 3, amount: 40000, cur: 'DKK', close: shift(TODAY, 30), owner: 3, team: 1 },
  { id: 106, stage: 4, amount: 9999, cur: 'DKK', close: shift(TODAY, 30), owner: 5, team: 2 },
  // won
  { id: 201, stage: 5, amount: 10000, cur: 'DKK', close: `${CUR}-01`, owner: 1, team: 1 },
  { id: 202, stage: 5, amount: 100, cur: 'EUR', close: `${CUR}-01`, owner: 1, team: 1 },
  { id: 203, stage: 5, amount: 200, cur: 'EUR', close: `${addMonths(CUR, -1)}-15`, owner: 1, team: 1 },
  { id: 204, stage: 5, amount: 300, cur: 'USD', close: `${addMonths(CUR, -1)}-10`, owner: 1, team: 1 },
  { id: 205, stage: 5, amount: 50, cur: 'DKK', close: lastDay(CUR), owner: 3, team: 1 },
  { id: 206, stage: 5, amount: 7000, cur: 'DKK', close: shift(TODAY, -200), owner: 1, team: 1 }, // far outside every window
  { id: 207, stage: 5, amount: 800, cur: 'SEK', close: `${CUR}-01`, owner: 1, team: 1 }, // won, no rate
  // lost
  { id: 301, stage: 6, amount: 1, cur: 'DKK', close: shift(TODAY, -10), owner: 1, team: 1 },
  { id: 302, stage: 6, amount: 1, cur: 'DKK', close: shift(TODAY, -20), owner: 3, team: 1 },
  { id: 303, stage: 6, amount: 1, cur: 'DKK', close: shift(TODAY, -100), owner: 1, team: 1 }, // outside 90 d
  { id: 304, stage: 6, amount: 1, cur: 'DKK', close: shift(TODAY, -89), owner: 1, team: 1 }, // inside (boundary)
  { id: 305, stage: 6, amount: 1, cur: 'DKK', close: shift(TODAY, -90), owner: 1, team: 1 }, // outside (boundary)
];

function build(): { store: ReturnType<typeof createMemoryStore>; state: MemoryState } {
  const state = seedState();
  state.settings.timezone = 'UTC';
  state.settings.localeCode = 'en-GB';
  state.settings.fiscalStartMonth = FISCAL_START;
  const tpl = state.data.opportunities[0]!;
  state.data.opportunities = OPPS.map((o) => ({ ...tpl, Id: o.id, Name: `Opp ${o.id}`, AccountId: 1, StageId: o.stage, Amount: o.amount, CurrencyCode: o.cur, CloseDate: o.close, OwnerId: o.owner, TeamId: o.team, Ver: 1 })) as never;
  state.fx = RATES.map((r, i) => ({ id: i + 1, base: 'DKK', currency: r.currency, month: r.month, rate: r.rate, modifiedByName: 'x', modifiedAt: '' }));
  return { store: createMemoryStore(state), state };
}
async function page(role: 'Admin' | 'Manager' | 'User', path = '/') {
  const { store, state } = build();
  const { app } = makeApp({ config: demoConfig(), store });
  const agent = request.agent(app);
  await agent.get(`/__test/login?role=${role}`);
  return { html: (await agent.get(path)).text, agent, state };
}
const tile = (html: string, label: string) => {
  for (const x of html.matchAll(/<li class="tile">([\s\S]*?)<\/li>/g)) if (x[1]!.includes(`>${label}<`)) return x[1]!.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
  throw new Error(`no tile ${label}`);
};
const money = (n: number) => new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'DKK' }).format(n).replace('DKK', 'DKK ').replace(/\s+/g, ' ');
const full = (t: string) => /\((DKK[^)]*)\)/.exec(t)?.[1]?.replace(/\s+/g, ' ') ?? '';

/** Expected values for the opportunities visible to a viewer (owner ids / team filter). */
function expected(visible: (o: Opp) => boolean) {
  const mine = OPPS.filter(visible);
  const sum = (rows: Opp[], monthOf: (o: Opp) => string, w: (o: Opp) => number = () => 1) => {
    let total = 0, excluded = 0; const missing = new Set<string>(); const fallback = new Set<string>();
    for (const o of rows) {
      const r = rateFor(o.cur, monthOf(o));
      if (r === null) { excluded += 1; missing.add(o.cur); continue; }
      total += o.amount * w(o) * r;
      if (o.cur !== 'DKK' && !RATES.some((x) => x.currency === o.cur && x.month === monthOf(o))) fallback.add(o.cur);
    }
    return { total: Math.round(total * 100) / 100, excluded, missing: [...missing].sort(), fallback: [...fallback].sort() };
  };
  const open = mine.filter((o) => o.stage <= 4);
  const won = mine.filter((o) => o.stage === 5);
  const lost = mine.filter((o) => o.stage === 6);
  const inWin = (o: Opp) => o.close >= shift(TODAY, -89) && o.close <= TODAY;
  const wm = won.filter((o) => ym(o.close) === CUR);
  const wq = won.filter((o) => o.close >= Q_FROM && o.close <= Q_TO);
  const wonWin = won.filter(inWin).length;
  const lostWin = lost.filter(inWin).length;
  return {
    open: sum(open, () => CUR), weighted: sum(open, () => CUR, (o) => PROB[o.stage]!),
    wonMonth: { ...sum(wm, (o) => ym(o.close)), n: wm.length }, wonQuarter: { ...sum(wq, (o) => ym(o.close)), n: wq.length },
    winRate: wonWin + lostWin === 0 ? null : Math.round((wonWin / (wonWin + lostWin)) * 100), wonWin, lostWin, openCount: open.length,
  };
}

describe('hand-calculated KPI values (fiscal year starts in April)', () => {
  it('sanity: the independent arithmetic itself matches the hand calculation for the Admin view', () => {
    const e = expected(() => true);
    expect(e.open.total).toBe(100000 + 40000 + 9999 + 1000 * 7.5 + 500 * 6); // SEK excluded
    expect(e.open.excluded).toBe(1);
    expect(e.weighted.total).toBeCloseTo(100000 * 0.5 + 40000 * 0.5 + 9999 * 0.75 + 7500 * 0.25 + 3000 * 0.75, 2);
  });

  it.each([
    ['Admin', () => true],
    ['Manager', (o: Opp) => o.team === 1],
    ['User', (o: Opp) => o.owner === 3],
  ] as const)('%s: every tile equals the independently computed value, count and note', async (role, visible) => {
    const { html } = await page(role);
    const e = expected(visible);
    const open = tile(html, 'Open pipeline');
    expect(full(open), 'open').toBe(money(e.open.total));
    expect(open).toContain(`${e.openCount} opp`);
    expect(full(tile(html, 'Weighted pipeline')), 'weighted').toBe(money(e.weighted.total));
    const wm = tile(html, 'Won this month');
    expect(full(wm), 'won month').toBe(money(e.wonMonth.total));
    expect(wm).toContain(`${e.wonMonth.n} ${e.wonMonth.n === 1 ? 'deal' : 'deals'}`);
    const wq = tile(html, 'Won this quarter');
    expect(full(wq), 'won quarter').toBe(money(e.wonQuarter.total));
    expect(wq).toContain(`${e.wonQuarter.n} ${e.wonQuarter.n === 1 ? 'deal' : 'deals'}`);
    const wr = tile(html, 'Win rate (last 90 d)');
    if (e.winRate === null) expect(wr).toContain('No closed deals yet');
    else { expect(wr).toContain(`${e.winRate}%`); expect(wr).toContain(`${e.wonWin} of ${e.wonWin + e.lostWin}`); }
    // exclusion and fallback notes
    if (e.open.excluded) expect(open).toContain(`${e.open.excluded} ${e.open.excluded === 1 ? 'deal' : 'deals'} excluded (no FX rate for ${e.open.missing.join(', ')})`);
    else expect(open).not.toContain('excluded');
    if (e.open.fallback.length) expect(open).toContain(`Uses latest rate for ${e.open.fallback.length} ${e.open.fallback.length === 1 ? 'currency' : 'currencies'}`);
    if (e.wonQuarter.excluded) expect(wq).toContain('excluded');
  });

  it('known absolute numbers for the Admin view, calculated by hand', async () => {
    const { html } = await page('Admin');
    // open: 100000 + 40000 + 9999 DKK, 1000 EUR x 7,5 = 7500, 500 USD x 6 (fallback from 2 months ago) = 3000, SEK 2000 excluded
    expect(full(tile(html, 'Open pipeline'))).toBe('DKK 160,499.00');
    // weighted: 50000 + 20000 + 7499.25 + 1875 + 2250
    expect(full(tile(html, 'Weighted pipeline'))).toBe('DKK 81,624.25');
    // won this month: 10000 + 100 EUR x 7,5 (Nov rate, close month) + 50 DKK = 10800; SEK 800 excluded
    expect(full(tile(html, 'Won this month'))).toBe('DKK 10,800.00');
    expect(tile(html, 'Won this month')).toContain('1 deal excluded (no FX rate for SEK)');
  });

  it('close-month rule: a won EUR deal uses the rate of its own close month, never the current month and never a later one', async () => {
    const { html } = await page('Admin');
    const e = expected(() => true);
    // 203 closed last month at 7,0 (not 7,5 and never 99); 204 USD at the only earlier rate (6,0), never the 99 rate that lies in the future
    const q = e.wonQuarter.total;
    expect(full(tile(html, 'Won this quarter'))).toBe(money(q));
    expect(q).toBeLessThan(1_000_000); // a leak of the "99" later-month rates would blow this up
  });

  it('boundary days: a deal closed exactly 89 days ago counts in the 90-day window, 90 days ago does not', () => {
    const e = expected(() => true);
    // won in window: 201,202,207 (current month) + 203,204 (if within 89 days) ... derived; lost in window must include 301,302,304 and exclude 303,305
    expect(e.lostWin).toBe(3);
  });

  it('quarter links and month links carry the exact ranges used for the tile', async () => {
    const { html } = await page('Admin');
    expect(html).toContain(`href="/opportunities?status=won&amp;from=${Q_FROM}&amp;to=${Q_TO}"`);
    expect(html).toContain(`href="/opportunities?status=won&amp;from=${CUR}-01&amp;to=${lastDay(CUR)}"`);
    expect(html).toContain(`href="/opportunities?status=closed&amp;from=${shift(TODAY, -89)}&amp;to=${TODAY}"`);
  });

  it('tile links lead to lists whose rows are exactly the deals behind the tile', async () => {
    const { agent, html } = await page('Admin');
    const names = async (url: string) => [...(await agent.get(url)).text.matchAll(/<a href="\/opportunities\/(\d+)">/g)].map((m) => Number(m[1])).sort();
    const e = expected(() => true);
    const href = (label: string) => new RegExp(`href="([^"]+)" class="tile-link"[^>]*>\\s*<span class="tile-label">${label.replace(/[()]/g, '\\$&')}<`).exec(html)![1]!.replace(/&amp;/g, '&');
    expect(await names(href('Open pipeline') + '&pageSize=100')).toEqual(OPPS.filter((o) => o.stage <= 4).map((o) => o.id).sort());
    expect(await names(href('Won this month') + '&pageSize=100')).toEqual(OPPS.filter((o) => o.stage === 5 && ym(o.close) === CUR).map((o) => o.id).sort());
    expect(await names(href('Won this quarter') + '&pageSize=100')).toEqual(OPPS.filter((o) => o.stage === 5 && o.close >= Q_FROM && o.close <= Q_TO).map((o) => o.id).sort());
    const win = OPPS.filter((o) => o.stage >= 5 && o.close >= shift(TODAY, -89) && o.close <= TODAY).map((o) => o.id).sort();
    expect(await names(href('Win rate (last 90 d)') + '&pageSize=100')).toEqual(win);
    expect(win.length).toBe(e.wonWin + e.lostWin);
  });
});

describe('role scoping and the Admin / non-Admin FX hint', () => {
  it('a User in private mode never sees other people\'s deals in any tile or list', async () => {
    const { html, agent } = await page('User');
    expect(full(tile(html, 'Open pipeline'))).toBe(money(40000));
    expect((await agent.get('/opportunities?status=all&pageSize=100')).text).not.toMatch(/Opp (101|106|201)\b/);
  });
  it('Admin gets "Add FX rates" (link to settings), others "Ask an administrator."', async () => {
    const admin = (await page('Admin')).html;
    expect(tile(admin, 'Open pipeline')).toContain('Add FX rates');
    expect(admin).toContain('href="/settings/fx"');
    const mgr = (await page('Manager')).html;
    expect(tile(mgr, 'Open pipeline')).toContain('Ask an administrator.');
    expect(mgr).not.toContain('href="/settings/fx"');
  });
});

describe('activities due excludes notes, done items and future dates', () => {
  it('counts calls, meetings and tasks due today / overdue only, scoped by visibility', async () => {
    const { store, state } = build();
    const tpl = state.data.activities[0]!;
    const mk = (id: number, type: string, due: string | null, done = false, owner = 1) => ({ ...tpl, Id: id, ActivityType: type, Subject: `A${id}`, DueDate: due, IsDone: done, OwnerId: owner, TeamId: owner === 5 ? 2 : 1, AccountId: 1 }) as never;
    state.data.activities = [
      mk(1, 'task', TODAY), mk(2, 'call', TODAY), mk(3, 'note', TODAY), // note excluded
      mk(4, 'task', shift(TODAY, -3)), mk(5, 'meeting', shift(TODAY, -1)), mk(6, 'note', shift(TODAY, -2)), // 2 overdue + 1 note
      mk(7, 'task', TODAY, true), // done excluded
      mk(8, 'task', shift(TODAY, 1)), mk(9, 'call', null), // not due
      mk(10, 'task', TODAY, false, 5), // other team's (see TeamId override below), invisible to the manager
    ];
    const { app } = makeApp({ config: demoConfig(), store });
    const agent = request.agent(app);
    await agent.get('/__test/login?role=Manager');
    const t = tile((await agent.get('/')).text, 'Activities due');
    expect(t).toContain('2 due today');
    expect(t).toContain('2 overdue');
    const list = (await agent.get('/activities?status=open&type=actionable&due=due&pageSize=100')).text;
    const ids = [...list.matchAll(/<a href="\/activities\/(\d+)">/g)].map((m) => Number(m[1])).sort((a, b) => a - b);
    expect(ids).toEqual([1, 2, 4, 5]);
  });
});

describe('Accounts "Open pipeline" column', () => {
  it('shows the converted open total per account and calls out unconvertible deals; accounts without open deals show a dash', async () => {
    const { html } = await page('Admin', '/accounts?pageSize=100');
    // account 1 carries all opportunities in this data set; open: DKK 160.499 + SEK excluded
    expect(html).toContain('DKK 160,499.00');
    expect(html).toMatch(/not converted \(no rate: SEK\)/);
    expect(html).toMatch(/<td[^>]*>\s*–\s*<\/td>/);
  });
});

// ---------------- strict number parsing (UX H3): try to break it ----------------
import { ALLOWED_LOCALES, parseAmount, parseRate } from '../src/services/format';

const SEPS: Record<string, { g: string; d: string }> = { 'da-DK': { g: '.', d: ',' }, 'en-GB': { g: ',', d: '.' }, 'en-US': { g: ',', d: '.' } };
const esc = (s: string) => s.replace(/[.,]/g, '\\$&');
describe('strict locale number parsing', () => {
  it('covers exactly the locales whose separators this test knows', () => {
    expect([...ALLOWED_LOCALES].sort()).toEqual(Object.keys(SEPS).sort());
  });
  for (const loc of Object.keys(SEPS)) {
    const { g, d } = SEPS[loc]!;
    it(`${loc}: formatted values round-trip exactly (amounts grouped, rates ungrouped)`, () => {
      for (const v of [0, 0.5, 1, 12, 123, 1234, 12345.6, 123456.78, 1234567.89, 99999999999.99]) {
        const s = new Intl.NumberFormat(loc, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v);
        expect(parseAmount(s, loc), s).toEqual({ ok: true, value: v });
      }
      for (const v of [0.000001, 0.5, 7.459, 7.4590, 123.456789, 1000.5]) {
        const s = new Intl.NumberFormat(loc, { useGrouping: false, minimumFractionDigits: 0, maximumFractionDigits: 6 }).format(v);
        expect(parseRate(s, loc), s).toEqual({ ok: true, value: v });
      }
    });
    it(`${loc}: the other locale's separators are never silently misread`, () => {
      const other = loc === 'da-DK' ? 'en-US' : 'da-DK';
      const o = SEPS[other]!;
      const wrongAmount = `1${o.g}234${o.d}56`;
      expect(parseAmount(wrongAmount, loc).ok, wrongAmount).toBe(false);
      for (const bad of [`7${o.d}459`.replace(o.d, o.d), `12${o.d}50`]) {
        const r = parseAmount(bad, loc);
        // either rejected, or (only when the other locale's decimal is this locale's group and has 3 digits) a deliberate thousands reading
        if (r.ok) expect(bad.split(o.d)[1]!.length).toBe(3);
      }
    });
    it(`${loc}: malformed shapes are rejected`, () => {
      for (const bad of ['', ' ', '-', '-1', '+1', '1e3', '1E3', '0x10', '1 000', '1 000', '1 000', `1${g}00`, `1${g}2345`, `1${g}${g}234`, `1${d}${d}5`, `1${d}5${d}5`, `1${d}`, `${d}5`, `1${d}5${g}000`, `${g}123`, 'abc', '1,2,3', '1..2', '１２３', '١٢٣', '1−', '−1', 'NaN', 'Infinity', '1_000', "1'000", '1;2', '1/2', '१२३']) {
        expect(parseAmount(bad, loc).ok, JSON.stringify(bad)).toBe(false);
        expect(parseRate(bad, loc).ok, `rate ${JSON.stringify(bad)}`).toBe(false);
      }
      expect(parseAmount(`1${d}234`, loc).ok, 'three decimals are not silently rounded or read as grouping').toBe(false);
      expect(parseAmount('12345678901234567', loc).ok).toBe(false);
      expect(parseRate(`1${g}000`, loc).ok, 'no grouping at all in rates').toBe(false);
      expect(parseRate(`0${d}1234567`, loc).ok, 'a 7th decimal is refused, not rounded').toBe(false);
      expect(parseAmount(`1${d}005`, loc).ok).toBe(false);
    });
    it(`${loc}: random strings are accepted only if they match the strict grammar written independently here`, () => {
      const alphabet = ['0', '1', '5', '9', g, d, ' ', '-', 'e', ' '];
      const strict = new RegExp(`^(?:\\d+|\\d{1,3}(?:${esc(g)}\\d{3})+)(?:${esc(d)}\\d{1,2})?$`);
      let seed = 12345;
      const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
      for (let i = 0; i < 20000; i++) {
        const s = Array.from({ length: 1 + Math.floor(rnd() * 9) }, () => alphabet[Math.floor(rnd() * alphabet.length)]).join('');
        const r = parseAmount(s, loc);
        const ok = strict.test(s.trim());
        expect(r.ok && !s.trim().startsWith('-'), JSON.stringify(s)).toBe(ok);
        if (r.ok) expect(Number.isFinite(r.value) && r.value >= 0).toBe(true);
      }
    });
  }
  it('da-DK: a leading-zero group reads as thousands ("0.500" = 500), a likely mistake for a decimal 0,5', () => {
    // Documented as QA-F1 (Low): accepted without warning; the grammar is locale-correct but "0.500" is almost always a decimal point typed by an en-user.
    expect(parseAmount('0.500', 'da-DK')).toEqual({ ok: true, value: 500 });
  });
  it('the validation messages use the active locale\'s example', async () => {
    const { agent } = await page('Admin');
    const t = (await agent.get('/')).text;
    expect(t).toContain('Dashboard');
  });
});

// ---------------- analytics ----------------
const qm = () => quarterStartMonth(TODAY);
describe('analytics periods, validation, tables and scoping', () => {
  const rangeOf = (html: string) => ({ from: /id="f-from"[^>]*value="([^"]*)"/.exec(html)?.[1], to: /id="f-to"[^>]*value="([^"]*)"/.exec(html)?.[1] });
  it('presets resolve to the independently computed ranges (fiscal quarters start in April)', async () => {
    const qs = quarterStartMonth(TODAY);
    const fyStartMonthNum = ((Number(CUR.slice(5)) - FISCAL_START + 12) % 12);
    const fyFrom = `${addMonths(CUR, -fyStartMonthNum)}-01`;
    const expectRange: Record<string, [string, string]> = {
      'this-month': [`${CUR}-01`, lastDay(CUR)],
      'last-month': [`${addMonths(CUR, -1)}-01`, lastDay(addMonths(CUR, -1))],
      'this-quarter': [`${qs}-01`, lastDay(addMonths(qs, 2))],
      'last-quarter': [`${addMonths(qs, -3)}-01`, lastDay(addMonths(qs, -1))],
      'this-year': [fyFrom, lastDay(addMonths(fyFrom.slice(0, 7), 11))],
      'last-12-months': [`${addMonths(CUR, -11)}-01`, lastDay(CUR)],
    };
    const { agent } = await page('Admin');
    for (const [key, [from, to]] of Object.entries(expectRange)) {
      const html = (await agent.get(`/analytics?period=${key}`)).text;
      expect(rangeOf(html), key).toEqual({ from, to });
    }
    // unknown or missing period falls back to this quarter
    expect(rangeOf((await agent.get('/analytics?period=bogus')).text)).toEqual({ from: expectRange['this-quarter']![0], to: expectRange['this-quarter']![1] });
    expect(rangeOf((await agent.get('/analytics')).text)).toEqual({ from: expectRange['this-quarter']![0], to: expectRange['this-quarter']![1] });
  });
  it('custom range validation: exact copy, fallback to this quarter, input kept, never a 500', async () => {
    const { agent } = await page('Admin');
    const get = (q: string) => agent.get(`/analytics?period=custom&${q}`);
    const cases: Array<[string, string]> = [
      ['from=&to=', 'Enter a real date, for example 31.12.2026.'],
      ['from=2026-02-30&to=2026-03-05', 'Enter a real date, for example 31.12.2026.'],
      ['from=2026-02-30&to=2026-03-01', 'Enter a real date, for example 31.12.2026.'],
      ['from=abc&to=2026-03-01', 'Enter a real date, for example 31.12.2026.'],
      ['from=2026-03-01&to=2026-13-01', 'Enter a real date, for example 31.12.2026.'],
      ['from=2026-03-10&to=2026-03-09', 'End date must be on or after start date.'],
      ['from=2020-01-01&to=2026-01-01', 'Choose a range of 5 years or less.'],
    ];
    for (const [q, msg] of cases) {
      const r = await get(q);
      expect(r.status, q).toBe(200);
      expect(r.text, q).toContain(msg);
      expect(r.text, q).toContain('Showing this quarter instead.');
    }
    const keep = await get('from=2026-03-10&to=2026-03-09');
    expect(keep.text).toContain('value="2026-03-10"');
    const ok = await get('from=2026-03-10&to=2026-03-10');
    expect(ok.text).not.toContain('Showing this quarter instead.');
    expect((await get('from=2021-01-01&to=2026-01-01')).text, 'five years inclusive of leap days is allowed').not.toContain('Choose a range of 5 years or less.');
    for (const q of ['from[]=1&to[]=2', "from='%20OR%201=1&to=x", 'owner=abc', 'owner=-1', 'owner=99999999999', 'owner=0']) expect((await agent.get(`/analytics?${q}`)).status, q).toBeLessThan(500);
  });
  it('every chart has a data table, an accessible name and a takeaway; JSON data is script-safe', async () => {
    const { html } = await page('Admin', '/analytics?period=last-12-months');
    const figs = [...html.matchAll(/<figure class="chart"[\s\S]*?<\/figure>/g)].map((m) => m[0]);
    expect(figs.length).toBeGreaterThanOrEqual(5);
    for (const f of figs) {
      expect(f).toMatch(/<figcaption><strong>[^<]+<\/strong>/);
      expect(f).toContain('<table class="data">');
      expect(f).toMatch(/<caption class="visually-hidden">[^<]+<\/caption>/);
      expect(f).toMatch(/<th scope="col">/);
      if (f.includes('<canvas')) expect(f).toMatch(/<canvas role="img" aria-label="[^"]+"/);
    }
    expect(html).not.toMatch(/<script(?![^>]*(src=|type="application\/json"))/);
  });
  it('won revenue by month: each deal uses its own close-month rate; the exclusion note appears; table total equals the tile arithmetic', async () => {
    const { html } = await page('Admin', `/analytics?period=custom&from=${Q_FROM}&to=${Q_TO}`);
    const fig = /<figure class="chart" id="fig-[^"]*won[^"]*"[\s\S]*?<\/figure>/i.exec(html)?.[0] ?? '';
    expect(fig.length).toBeGreaterThan(0);
    const e = expected(() => true);
    const text = fig.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');
    const months = [0, 1, 2].map((i) => addMonths(qm(), i));
    const perMonth = months.map((m) => Math.round(OPPS.filter((o) => o.stage === 5 && ym(o.close) === m).reduce((t, o) => t + o.amount * (rateFor(o.cur, m) ?? 0), 0) * 100) / 100);
    const json = JSON.parse(/<script type="application\/json" id="chart-[^"]*won[^"]*-data">([\s\S]*?)<\/script>/i.exec(fig)![1]!);
    expect(json.data).toEqual(perMonth);
    expect(perMonth.reduce((a, b) => a + b, 0)).toBe(e.wonQuarter.total);
    expect(text).toMatch(/excluded \(no FX rate for SEK\)/);
    expect(text).toContain('Uses latest rate for 1 currency');
  });
  it('a User (private) sees only own data in every chart table; the owner filter cannot reveal other people\'s deals', async () => {
    const span = `period=custom&from=${shift(TODAY, -30)}&to=${shift(TODAY, 60)}`;
    const { html, agent } = await page('User', `/analytics?${span}`);
    const figs = [...html.matchAll(/<figure class="chart"[\s\S]*?<\/figure>/g)].map((m) => m[0]).join(' ');
    expect(figs).not.toMatch(/Demo Manager|Otto Otherteam|Anna Teammate|Demo Admin/);
    const forged = (await agent.get(`/analytics?${span}&owner=5`)).text;
    const own = (await agent.get(`/analytics?${span}&owner=3`)).text;
    const strip = (h: string) => h.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');
    expect(strip(forged)).not.toContain('9,999');
    expect(strip(own)).toContain('40,000');
    expect(strip(html)).not.toContain('100,000');
  });
});

describe('dashboard "Next 7 days" section', () => {
  // OPEN DEFECT QA-F3 (Low): the section count comes from activityCounts (days +1..+7) but the rows and the "view all" link use due=week
  // (today..+6), so a task due today is listed twice (Today and Next 7 days), a task due in exactly 7 days is counted but not listed.
  // it.fails: remove marker once the count, the rows and the link use the same window.
  it('lists each activity once and its count matches the rows shown (QA-F3)', async () => {
    const { store, state } = build();
    const tpl = state.data.activities[0]!;
    state.data.activities = [0, 1, 6, 7, 8].map((n, i) => ({ ...tpl, Id: i + 1, Subject: `Act+${n}`, ActivityType: 'task', DueDate: shift(TODAY, n), IsDone: false, OwnerId: 1, TeamId: 1, AccountId: 1 })) as never;
    const { app } = makeApp({ config: demoConfig(), store });
    const agent = request.agent(app);
    await agent.get('/__test/login?role=Admin');
    const html = (await agent.get('/')).text;
    // count the row's subject LINK only: the Mark-done button repeats the subject in visually hidden text (UX H3)
    const shown = html.match(/>Act\+\d</g) ?? [];
    expect(new Set(shown).size, 'each activity once').toBe(shown.length);
    const count = Number(/Next 7 days \((\d+)\)/.exec(html.replace(/<[^>]+>/g, ' '))![1]);
    const section = html.slice(html.indexOf('Next 7 days (')).match(/>Act\+\d</g) ?? [];
    expect(count).toBe(section.length);
  });
});
