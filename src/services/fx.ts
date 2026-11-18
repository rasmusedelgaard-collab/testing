import type { FxRateRow } from '../domain/types';
export type { FxRateRow };
export type Conversion = { ok: true; value: number; fallback: boolean } | { ok: false };

export interface Converter {
  base: string;
  /** Converts at the rate of `month`; falls back to the latest EARLIER month; never guesses a rate. */
  convert(amount: number, currency: string, month: string): Conversion;
}

/**
 * ADR-0005: rate semantics are 1 CURRENCY = rate BASE. The base currency is always exactly 1 and is never
 * looked up. No rate at or before the month means "not convertible" (the caller must exclude and say so, never use 1:1).
 */
export function makeConverter(base: string, rates: FxRateRow[]): Converter {
  const byCurrency = new Map<string, FxRateRow[]>();
  for (const r of rates) {
    if (r.currency === base || !(r.rate > 0)) continue;
    const list = byCurrency.get(r.currency) ?? [];
    list.push(r);
    byCurrency.set(r.currency, list);
  }
  for (const list of byCurrency.values()) list.sort((a, b) => b.month.localeCompare(a.month)); // newest first
  return {
    base,
    convert(amount, currency, month) {
      if (currency === base) return { ok: true, value: amount, fallback: false };
      const list = byCurrency.get(currency);
      const hit = list?.find((r) => r.month <= month);
      if (!hit) return { ok: false };
      return { ok: true, value: amount * hit.rate, fallback: hit.month !== month };
    },
  };
}

export interface MoneyRow { currency: string; count: number; amount: number }
export interface Converted {
  total: number;
  /** Deals left out of the total because no rate exists, per currency. */
  excluded: Array<{ currency: string; count: number }>;
  /** Currencies for which an earlier month's rate was used. */
  fallbackCurrencies: string[];
}

const round2 = (n: number) => Math.round((n + Number.EPSILON) * 100) / 100;

/** Sums rows in the base currency. `monthOf` picks the rate month per row; `weight` scales a row (e.g. stage probability). */
export function convertRows<R extends MoneyRow>(rows: R[], conv: Converter, monthOf: (r: R) => string, weight: (r: R) => number = () => 1): Converted {
  let total = 0;
  const excluded = new Map<string, number>();
  const fallback = new Set<string>();
  for (const r of rows) {
    const c = conv.convert(r.amount * weight(r), r.currency, monthOf(r));
    if (!c.ok) {
      excluded.set(r.currency, (excluded.get(r.currency) ?? 0) + r.count);
      continue;
    }
    total += c.value;
    if (c.fallback) fallback.add(r.currency);
  }
  return {
    total: round2(total),
    excluded: [...excluded].map(([currency, count]) => ({ currency, count })).sort((a, b) => a.currency.localeCompare(b.currency)),
    fallbackCurrencies: [...fallback].sort(),
  };
}

/** "3 opportunities in SEK not included: no FX rate" (UI spec 6), or '' when nothing was excluded. */
export function exclusionNote(c: Converted): string {
  if (c.excluded.length === 0) return '';
  const parts = c.excluded.map((e) => `${e.count} ${e.count === 1 ? 'opportunity' : 'opportunities'} in ${e.currency}`);
  return `${parts.join(', ')} not included: no FX rate`;
}
export function fallbackNote(c: Converted): string {
  const n = c.fallbackCurrencies.length;
  return n === 0 ? '' : `Uses latest rate for ${n} ${n === 1 ? 'currency' : 'currencies'}`;
}
