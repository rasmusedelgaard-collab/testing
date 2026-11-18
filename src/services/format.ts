import type { Settings } from '../domain/types';

export const ALLOWED_LOCALES = ['da-DK', 'en-GB', 'en-US'];

export interface Formatter {
  money(amount: number, currency: string): string;
  date(iso: string | null | undefined): string;
  dateTime(iso: string | null | undefined): string;
  /** Plain amount for form inputs, in the locale's decimal separator, no grouping. */
  amountInput(amount: number): string;
}

function safeLocale(code: string): string {
  return ALLOWED_LOCALES.includes(code) ? code : 'en-GB';
}

export function makeFormatter(s: Pick<Settings, 'localeCode' | 'timezone'>): Formatter {
  const locale = safeLocale(s.localeCode);
  let tz = s.timezone;
  try {
    new Intl.DateTimeFormat(locale, { timeZone: tz });
  } catch {
    tz = 'UTC';
  }
  const dateFmt = new Intl.DateTimeFormat(locale, { timeZone: 'UTC', year: 'numeric', month: '2-digit', day: '2-digit' });
  const dateTimeFmt = new Intl.DateTimeFormat(locale, { timeZone: tz, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
  return {
    money: (amount, currency) => `${currency} ${new Intl.NumberFormat(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(amount)}`,
    // Date-only values are calendar dates: format them in UTC so they never shift a day.
    date: (iso) => (iso && /^\d{4}-\d{2}-\d{2}/.test(iso) ? dateFmt.format(new Date(`${iso.slice(0, 10)}T00:00:00Z`)) : ''),
    dateTime: (iso) => (iso ? dateTimeFmt.format(new Date(iso)) : ''),
    amountInput: (amount) => new Intl.NumberFormat(locale, { useGrouping: false, minimumFractionDigits: 0, maximumFractionDigits: 2 }).format(amount),
  };
}

export function todayIn(timezone: string, now: Date = new Date()): string {
  try {
    return new Intl.DateTimeFormat('en-CA', { timeZone: timezone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(now);
  } catch {
    return now.toISOString().slice(0, 10);
  }
}

export type AmountResult = { ok: true; value: number } | { ok: false; reason: 'format' | 'negative' };

/** Parses an amount typed in the Settings locale. Grouping separators are optional; at most 2 decimals. */
export function parseAmount(input: string, localeCode: string): AmountResult {
  const parts = new Intl.NumberFormat(safeLocale(localeCode)).formatToParts(1234567.5);
  const group = parts.find((p) => p.type === 'group')?.value ?? ',';
  const decimal = parts.find((p) => p.type === 'decimal')?.value ?? '.';
  let s = input.trim().replace(/[\s\u00a0\u202f]/g, '');
  if (s.startsWith('-')) return { ok: false, reason: 'negative' };
  s = s.split(group).join('').replace(decimal, '.');
  if (!/^\d+(\.\d{1,2})?$/.test(s)) return { ok: false, reason: 'format' };
  const value = Number(s);
  if (!Number.isFinite(value) || value > 99_999_999_999_999) return { ok: false, reason: 'format' };
  return { ok: true, value };
}

export function isRealDate(s: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(s)) return false;
  const d = new Date(`${s}T00:00:00Z`);
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === s && Number(s.slice(0, 4)) >= 1900;
}
