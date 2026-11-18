import { describe, expect, it } from 'vitest';
import type { AggRow, Stage } from '../src/domain/types';
import { activitiesByType, oppsByLeadSource, pipelineByStage, winRateByOwner, wonRevenueByMonth } from '../src/services/charts';
import { convertRows, exclusionNote, fallbackNote, makeConverter } from '../src/services/fx';
import { makeFormatter } from '../src/services/format';
import { computeTiles, pipelineTotals, winRate, winRateRange } from '../src/services/kpi';
import { addDays, fiscalYearRange, monthsBetween, quarterRange, resolvePeriod } from '../src/services/periods';

const row = (over: Partial<AggRow>): AggRow => ({ stageType: 'open', stageId: null, ownerId: null, ownerName: null, leadSource: null, month: null, accountId: null, currency: 'DKK', count: 1, amount: 100, ...over });
const stages: Stage[] = [
  { id: 1, name: 'Qual', sortOrder: 1, probability: 10, type: 'open' }, { id: 3, name: 'Prop', sortOrder: 3, probability: 50, type: 'open' },
  { id: 5, name: 'Won', sortOrder: 5, probability: 100, type: 'won' }, { id: 6, name: 'Lost', sortOrder: 6, probability: 0, type: 'lost' },
];

describe('FX conversion (ADR-0005)', () => {
  const conv = makeConverter('DKK', [
    { currency: 'EUR', month: '2026-09', rate: 7.4 }, { currency: 'EUR', month: '2026-11', rate: 7.5 }, { currency: 'USD', month: '2026-10', rate: 6.9 },
    { currency: 'DKK', month: '2026-11', rate: 99 }, // the base currency is never looked up, even if a row exists
  ]);
  it('base currency is exactly 1, never converted, never flagged', () => {
    expect(conv.convert(123.45, 'DKK', '2026-11')).toEqual({ ok: true, value: 123.45, fallback: false });
    expect(conv.convert(0.1 + 0.2, 'DKK', '1999-01')).toEqual({ ok: true, value: 0.1 + 0.2, fallback: false });
  });
  it('uses the exact month rate', () => {
    expect(conv.convert(100, 'EUR', '2026-11')).toEqual({ ok: true, value: 750, fallback: false });
  });
  it('falls back to the latest EARLIER month and flags it', () => {
    expect(conv.convert(100, 'EUR', '2026-10')).toEqual({ ok: true, value: 740, fallback: true });
    expect(conv.convert(100, 'EUR', '2027-03')).toEqual({ ok: true, value: 750, fallback: true });
  });
  it('never uses a LATER rate and never guesses 1:1', () => {
    expect(conv.convert(100, 'EUR', '2026-08')).toEqual({ ok: false });
    expect(conv.convert(100, 'USD', '2026-09')).toEqual({ ok: false });
    expect(conv.convert(100, 'SEK', '2026-11')).toEqual({ ok: false });
  });
  it('ignores non-positive rates', () => {
    expect(makeConverter('DKK', [{ currency: 'EUR', month: '2026-11', rate: 0 }]).convert(1, 'EUR', '2026-11')).toEqual({ ok: false });
  });
  it('convertRows sums, excludes unconvertible deals with counts, and reports fallbacks', () => {
    const rows = [row({ amount: 100 }), row({ currency: 'EUR', amount: 10, month: '2026-10' }), row({ currency: 'SEK', amount: 50, count: 3 }), row({ currency: 'SEK', amount: 5, count: 2 })];
    const c = convertRows(rows, conv, (r) => r.month ?? '2026-11');
    expect(c.total).toBe(174);
    expect(c.excluded).toEqual([{ currency: 'SEK', count: 5 }]);
    expect(c.fallbackCurrencies).toEqual(['EUR']);
    expect(exclusionNote(c)).toBe('5 deals excluded (no FX rate for SEK)');
    expect(fallbackNote(c)).toBe('Uses latest rate for 1 currency');
    expect(exclusionNote(convertRows([], conv, () => '2026-11'))).toBe('');
  });
  it('singular wording and rounding to cents', () => {
    expect(exclusionNote({ total: 0, excluded: [{ currency: 'SEK', count: 1 }], fallbackCurrencies: [] })).toBe('1 deal excluded (no FX rate for SEK)');
    const c = convertRows([row({ currency: 'EUR', amount: 0.333, month: '2026-11' })], conv, (r) => r.month!);
    expect(c.total).toBe(2.5);
  });
});

describe('periods: fiscal quarters, months and timezone-independent date maths', () => {
  it('calendar fiscal year (start month 1)', () => {
    expect(quarterRange('2026-11-18', 1)).toEqual({ from: '2026-10-01', to: '2026-12-31' });
    expect(quarterRange('2026-01-01', 1)).toEqual({ from: '2026-01-01', to: '2026-03-31' });
    expect(quarterRange('2026-03-31', 1)).toEqual({ from: '2026-01-01', to: '2026-03-31' });
    expect(quarterRange('2026-04-01', 1)).toEqual({ from: '2026-04-01', to: '2026-06-30' });
  });
  it('fiscal year starting in April: quarter boundaries and year wrap', () => {
    expect(quarterRange('2026-04-01', 4)).toEqual({ from: '2026-04-01', to: '2026-06-30' });
    expect(quarterRange('2026-03-31', 4)).toEqual({ from: '2026-01-01', to: '2026-03-31' });
    expect(quarterRange('2026-02-10', 4)).toEqual({ from: '2026-01-01', to: '2026-03-31' });
    expect(quarterRange('2026-12-31', 4)).toEqual({ from: '2026-10-01', to: '2026-12-31' });
    expect(fiscalYearRange('2026-02-10', 4)).toEqual({ from: '2025-04-01', to: '2026-03-31' });
    expect(fiscalYearRange('2026-04-01', 4)).toEqual({ from: '2026-04-01', to: '2027-03-31' });
  });
  it('fiscal start in a month not divisible by 3 (November): Nov-Jan, Feb-Apr ...', () => {
    expect(quarterRange('2026-12-15', 11)).toEqual({ from: '2026-11-01', to: '2027-01-31' });
    expect(quarterRange('2027-01-31', 11)).toEqual({ from: '2026-11-01', to: '2027-01-31' });
    expect(quarterRange('2027-02-01', 11)).toEqual({ from: '2027-02-01', to: '2027-04-30' });
  });
  it('leap year and month ends', () => {
    expect(quarterRange('2028-02-29', 1)).toEqual({ from: '2028-01-01', to: '2028-03-31' });
    expect(addDays('2028-02-28', 1)).toBe('2028-02-29');
    expect(addDays('2027-02-28', 1)).toBe('2027-03-01');
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
  });
  it('win-rate window is exactly 90 calendar days including today', () => {
    expect(winRateRange('2026-11-18')).toEqual({ from: '2026-08-21', to: '2026-11-18' });
  });
  it('resolvePeriod presets', () => {
    expect(resolvePeriod({ period: 'this-month' }, '2026-11-18', 1).range).toEqual({ from: '2026-11-01', to: '2026-11-30' });
    expect(resolvePeriod({ period: 'last-month' }, '2026-01-10', 1).range).toEqual({ from: '2025-12-01', to: '2025-12-31' });
    expect(resolvePeriod({ period: 'last-quarter' }, '2026-01-10', 1).range).toEqual({ from: '2025-10-01', to: '2025-12-31' });
    expect(resolvePeriod({ period: 'last-12-months' }, '2026-11-18', 1).range).toEqual({ from: '2025-12-01', to: '2026-11-30' });
    expect(resolvePeriod({ period: 'this-year' }, '2026-11-18', 4).range).toEqual({ from: '2026-04-01', to: '2027-03-31' });
    expect(resolvePeriod({ period: 'nonsense' }, '2026-11-18', 1).key).toBe('this-quarter');
    expect(resolvePeriod({}, '2026-11-18', 1).range).toEqual({ from: '2026-10-01', to: '2026-12-31' });
  });
  it('custom range validation with the exact copy', () => {
    const ok = resolvePeriod({ period: 'custom', from: '2026-01-01', to: '2026-02-01' }, '2026-11-18', 1);
    expect(ok).toMatchObject({ ok: true, range: { from: '2026-01-01', to: '2026-02-01' } });
    expect(resolvePeriod({ period: 'custom', from: '2026-02-01', to: '2026-01-01' }, '2026-11-18', 1)).toMatchObject({ ok: false, error: 'End date must be on or after start date.' });
    expect(resolvePeriod({ period: 'custom', from: '2020-01-01', to: '2026-01-02' }, '2026-11-18', 1)).toMatchObject({ ok: false, error: 'Choose a range of 5 years or less.' });
    expect(resolvePeriod({ period: 'custom', from: '2021-01-01', to: '2026-01-01' }, '2026-11-18', 1).ok).toBe(true);
    expect(resolvePeriod({ period: 'custom', from: 'x', to: '' }, '2026-11-18', 1)).toMatchObject({ ok: false, error: 'Enter a real date, for example 31.12.2026.' });
  });
  it('monthsBetween lists every month and is capped', () => {
    expect(monthsBetween('2026-11-18', '2027-02-03')).toEqual(['2026-11', '2026-12', '2027-01', '2027-02']);
    expect(monthsBetween('2000-01-01', '2026-01-01', 12)).toHaveLength(12);
  });
});

describe('KPI maths', () => {
  const conv = makeConverter('DKK', [{ currency: 'EUR', month: '2026-11', rate: 7.5 }]);
  it('weighted pipeline = amount × stage probability, converted at the current month', () => {
    const open = [row({ stageId: 1, amount: 1000 }), row({ stageId: 3, amount: 2000 }), row({ stageId: 3, currency: 'EUR', amount: 100 })];
    const p = pipelineTotals(open, stages, conv, '2026-11');
    expect(p.total.total).toBe(3750);
    expect(p.weighted.total).toBe(100 + 1000 + 375);
    expect(p.count).toBe(3);
  });
  it('an unknown stage weighs 0, and nothing open gives 0', () => {
    expect(pipelineTotals([row({ stageId: 99, amount: 500 })], stages, conv, '2026-11').weighted.total).toBe(0);
    expect(pipelineTotals([], stages, conv, '2026-11').total.total).toBe(0);
  });
  it('win rate: normal, all won, all lost, zero denominator is null (no NaN)', () => {
    expect(winRate([row({ stageType: 'won', count: 14 }), row({ stageType: 'lost', count: 27 })])).toEqual({ won: 14, lost: 27, rate: 14 / 41 });
    expect(winRate([row({ stageType: 'won', count: 3 })]).rate).toBe(1);
    expect(winRate([row({ stageType: 'lost', count: 3 })]).rate).toBe(0);
    expect(winRate([]).rate).toBeNull();
    expect(winRate([row({ stageType: 'open', count: 9 })]).rate).toBeNull();
  });
  it('tiles: values, links, month vs quarter split, missing-rate notes', () => {
    const tiles = computeTiles({
      today: '2026-11-18', fiscalStartMonth: 1, stages, conv,
      openRows: [row({ stageId: 3, amount: 1000 }), row({ stageId: 3, currency: 'SEK', amount: 10, count: 2 })],
      quarterWonRows: [row({ stageType: 'won', month: '2026-10', amount: 500, count: 2 }), row({ stageType: 'won', month: '2026-11', amount: 200 }), row({ stageType: 'won', month: '2026-11', currency: 'EUR', amount: 10 })],
      winRateRows: [row({ stageType: 'won', count: 1 }), row({ stageType: 'lost', count: 3 })],
    });
    const by = Object.fromEntries(tiles.map((t) => [t.key, t]));
    expect(by.open).toMatchObject({ value: 1000, href: '/opportunities?status=open' });
    expect(by.open!.notes).toEqual(['2 deals excluded (no FX rate for SEK)']);
    expect(by.weighted!.value).toBe(500);
    expect(by['won-month']).toMatchObject({ value: 275, context: '2 deals', href: '/opportunities?status=won&from=2026-11-01&to=2026-11-30' });
    expect(by['won-quarter']).toMatchObject({ value: 775, context: '4 deals', href: '/opportunities?status=won&from=2026-10-01&to=2026-12-31' });
    expect(by['win-rate']).toMatchObject({ value: 25, context: '1 of 4', href: '/opportunities?status=closed&from=2026-08-21&to=2026-11-18' });
  });
  it('tiles with no data: zeros and an explicit "no closed deals" win rate', () => {
    const tiles = computeTiles({ today: '2026-11-18', fiscalStartMonth: 1, stages, conv, openRows: [], quarterWonRows: [], winRateRows: [] });
    expect(tiles.map((t) => t.value)).toEqual([0, 0, 0, 0, null]);
    expect(tiles[4]!.context).toBe('No closed deals yet');
  });
  it('won this month uses the fiscal-quarter rows by close month, so a quarter boundary never leaks', () => {
    const tiles = computeTiles({ today: '2026-04-01', fiscalStartMonth: 4, stages, conv, openRows: [], quarterWonRows: [row({ stageType: 'won', month: '2026-04', amount: 10 })], winRateRows: [] });
    expect(tiles.find((t) => t.key === 'won-quarter')!.href).toContain('from=2026-04-01&to=2026-06-30');
    expect(tiles.find((t) => t.key === 'won-month')!.value).toBe(10);
    const feb = computeTiles({ today: '2026-03-31', fiscalStartMonth: 4, stages, conv, openRows: [], quarterWonRows: [row({ stageType: 'won', month: '2026-03', amount: 10 })], winRateRows: [] });
    expect(feb.find((t) => t.key === 'won-month')!.href).toContain('from=2026-03-01&to=2026-03-31');
  });
});

describe('chart view-models', () => {
  const f = makeFormatter({ localeCode: 'en-GB', timezone: 'UTC' });
  const c = { f, locale: 'en-GB', base: 'DKK' };
  const conv = makeConverter('DKK', [{ currency: 'EUR', month: '2026-11', rate: 7.5 }]);
  it('pipeline by stage: one bar per open stage, takeaway, table and exclusion note', () => {
    const vm = pipelineByStage([row({ stageId: 1, amount: 100 }), row({ stageId: 3, amount: 900 }), row({ stageId: 3, currency: 'SEK', amount: 5 })], stages, conv, '2026-11', c);
    expect(vm.labels).toEqual(['Qual', 'Prop']);
    expect(vm.data).toEqual([100, 900]);
    expect(vm.takeaway.replace(/\s/g, ' ')).toBe('Prop holds the most value: DKK 900');
    expect(vm.notes).toEqual(['1 deal excluded (no FX rate for SEK)']);
    expect(vm.table.rows[1]).toMatchObject({ cells: ['Prop', '2', 'DKK 900.00'], flag: 'Not included: no FX rate' });
    expect(vm.empty).toBe(false);
  });
  it('empty data gives the exact empty copy and still has a table', () => {
    for (const vm of [pipelineByStage([], stages, conv, '2026-11', c), wonRevenueByMonth([], { from: '2026-10-01', to: '2026-11-30' }, conv, c), winRateByOwner([], c), activitiesByType([], c), oppsByLeadSource([], c)]) {
      expect(vm.empty).toBe(true);
      expect(vm.takeaway).toBe('No data for this period. Try a longer date range.');
    }
    expect(wonRevenueByMonth([], { from: '2026-10-01', to: '2026-11-30' }, conv, c).table.rows).toHaveLength(2);
  });
  it('won revenue by month fills empty months with 0 and converts each month at its own rate', () => {
    const vm = wonRevenueByMonth([row({ stageType: 'won', month: '2026-09', amount: 10, currency: 'EUR' }), row({ stageType: 'won', month: '2026-11', amount: 10, currency: 'EUR' })], { from: '2026-09-01', to: '2026-11-30' }, conv, c);
    expect(vm.data).toEqual([0, 0, 75]); // September has no rate at or before it: excluded, October has none either
    expect(vm.table.rows[0]!.flag).toBe('Not included: no FX rate');
    expect(vm.takeaway).toContain('Nov 2026');
  });
  it('win rate by owner sorted high to low with counts; no division by zero', () => {
    const vm = winRateByOwner([row({ stageType: 'won', ownerId: 1, ownerName: 'A', count: 1 }), row({ stageType: 'lost', ownerId: 1, ownerName: 'A', count: 3 }), row({ stageType: 'won', ownerId: 2, ownerName: 'B', count: 2 }), row({ stageType: 'open', ownerId: 3, ownerName: 'C' })], c);
    expect(vm.labels).toEqual(['B', 'A']);
    expect(vm.data).toEqual([100, 25]);
    expect(vm.table.rows[1]!.cells).toEqual(['A', '1', '3', '25.0%']);
    expect(vm.takeaway).toBe('B has the highest win rate: 100%');
  });
  it('activities by type has all four types; lead source groups missing as (none)', () => {
    expect(activitiesByType([{ type: 'call', count: 4 }, { type: 'note', count: 1 }], c).data).toEqual([4, 0, 0, 1]);
    const ls = oppsByLeadSource([row({ leadSource: null, count: 2 }), row({ leadSource: 'Web', count: 1 }), row({ leadSource: 'Web', count: 2, stageType: 'won' })], c);
    expect(ls.labels).toEqual(['Web', '(none)']);
    expect(ls.data).toEqual([3, 2]);
  });
});
