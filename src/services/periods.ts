import { isRealDate } from './format';

/** Calendar arithmetic on 'YYYY-MM-DD' strings (no time zones involved: callers pass "today" in the Settings timezone). */
const pad = (n: number) => String(n).padStart(2, '0');
const ymd = (d: Date) => `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
const parse = (s: string) => new Date(`${s}T00:00:00Z`);

export function addDays(date: string, days: number): string {
  const d = parse(date);
  d.setUTCDate(d.getUTCDate() + days);
  return ymd(d);
}
/** First day of the month that is `months` after the month of `date`. */
export function monthStartPlus(date: string, months: number): string {
  const d = parse(date);
  return ymd(new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + months, 1)));
}
export const monthOf = (date: string) => date.slice(0, 7);
/** Last day of the period that starts on `start` and lasts `months` months. */
export function endAfterMonths(start: string, months: number): string {
  return addDays(monthStartPlus(start, months), -1);
}

export interface Range { from: string; to: string }

export function monthRange(today: string): Range {
  const from = monthStartPlus(today, 0);
  return { from, to: endAfterMonths(from, 1) };
}

/** Fiscal quarter containing `today`. Quarters start in the fiscal start month and every 3 months after it. */
export function quarterRange(today: string, fiscalStartMonth: number): Range {
  const m = Number(today.slice(5, 7)); // 1-12
  const offset = (m - fiscalStartMonth + 12) % 12;
  const monthsBack = offset % 3;
  const from = monthStartPlus(today, -monthsBack);
  return { from, to: endAfterMonths(from, 3) };
}

export function fiscalYearRange(today: string, fiscalStartMonth: number): Range {
  const m = Number(today.slice(5, 7));
  const monthsBack = (m - fiscalStartMonth + 12) % 12;
  const from = monthStartPlus(today, -monthsBack);
  return { from, to: endAfterMonths(from, 12) };
}

export const PERIODS = ['this-month', 'last-month', 'this-quarter', 'last-quarter', 'this-year', 'last-12-months', 'custom'] as const;
export type PeriodKey = (typeof PERIODS)[number];
export const PERIOD_LABEL: Record<PeriodKey, string> = {
  'this-month': 'This month', 'last-month': 'Last month', 'this-quarter': 'This quarter', 'last-quarter': 'Last quarter', 'this-year': 'This year', 'last-12-months': 'Last 12 months', custom: 'Custom',
};

export type PeriodResult = { ok: true; key: PeriodKey; range: Range } | { ok: false; key: PeriodKey; range: Range; error: string };

/** Resolves the Analytics period. Invalid custom ranges fall back to this quarter and report the exact UI copy. */
export function resolvePeriod(input: { period?: string; from?: string; to?: string; shown?: string }, today: string, fiscalStartMonth: number): PeriodResult {
  let key = (PERIODS as readonly string[]).includes(input.period ?? '') ? (input.period as PeriodKey) : 'this-quarter';
  // The page pre-fills From/To with the preset's range and remembers which preset it filled them from (`shown`). If the
  // preset is unchanged but the dates were edited, the user means a custom range. If the preset itself was changed, the
  // old pre-filled dates are stale and ignored.
  if (key !== 'custom' && input.shown === key && (input.from || input.to)) {
    const preset = resolvePeriod({ period: key }, today, fiscalStartMonth).range;
    if (input.from !== preset.from || input.to !== preset.to) key = 'custom';
  }
  switch (key) {
    case 'this-month': return { ok: true, key, range: monthRange(today) };
    case 'last-month': return { ok: true, key, range: monthRange(monthStartPlus(today, -1)) };
    case 'this-quarter': return { ok: true, key, range: quarterRange(today, fiscalStartMonth) };
    case 'last-quarter': return { ok: true, key, range: quarterRange(monthStartPlus(today, -3), fiscalStartMonth) };
    case 'this-year': return { ok: true, key, range: fiscalYearRange(today, fiscalStartMonth) };
    case 'last-12-months': return { ok: true, key, range: { from: monthStartPlus(today, -11), to: endAfterMonths(monthStartPlus(today, -11), 12) } };
    case 'custom': {
      const fallback = quarterRange(today, fiscalStartMonth);
      const { from = '', to = '' } = input;
      if (!isRealDate(from) || !isRealDate(to)) return { ok: false, key, range: fallback, error: 'Enter a real date, for example 31.12.2026.' };
      if (to < from) return { ok: false, key, range: fallback, error: 'End date must be on or after start date.' };
      if (to > addDays(monthStartPlus(from, 0), 366 * 5 + 2)) return { ok: false, key, range: fallback, error: 'Choose a range of 5 years or less.' };
      return { ok: true, key, range: { from, to } };
    }
  }
}

/** Every 'YYYY-MM' from the month of `from` to the month of `to` (at most `max`, newest dropped beyond that). */
export function monthsBetween(from: string, to: string, max = 60): string[] {
  const out: string[] = [];
  for (let m = monthStartPlus(from, 0); m <= to && out.length < max; m = monthStartPlus(m, 1)) out.push(monthOf(m));
  return out;
}
