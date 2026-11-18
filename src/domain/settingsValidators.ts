import { contrastWithWhite } from '../services/color';
import { ALLOWED_LOCALES, parseRate, rateExample } from '../services/format';
import type { Errors } from './validators';

export const CURRENCIES = ['DKK', 'EUR', 'USD', 'GBP', 'SEK', 'NOK', 'CHF', 'PLN', 'CZK', 'HUF', 'ISK', 'CAD', 'AUD', 'JPY', 'CNY', 'INR', 'BRL', 'ZAR', 'SGD', 'AED'];
const s = (v: unknown) => (typeof v === 'string' ? v.trim() : '');
export type Result<T> = { values: T; errors: Errors };

export function timezones(): string[] {
  try {
    return (Intl as unknown as { supportedValuesOf(k: string): string[] }).supportedValuesOf('timeZone');
  } catch {
    return ['UTC', 'Europe/Copenhagen'];
  }
}

export function validateGeneral(body: Record<string, unknown>): Result<{ appName: string; accentColor: string }> {
  const errors: Errors = {};
  const appName = s(body.appName);
  if (!appName) errors.appName = 'Enter a CRM name.';
  else if (appName.length > 40) errors.appName = 'CRM name must be 40 characters or fewer.';
  const accent = s(body.accentColor);
  if (!/^#[0-9a-fA-F]{6}$/.test(accent)) errors.accentColor = 'Enter a colour like #1D4ED8.';
  else if (contrastWithWhite(accent) < 4.5) errors.accentColor = 'This colour is too light for text and buttons. Choose a darker shade.';
  return { values: { appName, accentColor: accent.toUpperCase() }, errors };
}

export interface LocaleValues { base: string; enabled: string[]; locale: string; timezone: string; firstDay: number; fiscalStart: number }
export function validateLocale(body: Record<string, unknown>): Result<LocaleValues> {
  const errors: Errors = {};
  const base = s(body.base);
  if (!CURRENCIES.includes(base)) errors.base = 'Choose a base currency.';
  const raw = body.enabled;
  const enabled = [...new Set((Array.isArray(raw) ? raw : raw ? [raw] : []).map(s).filter((c) => CURRENCIES.includes(c)))];
  if (!errors.base && !enabled.includes(base)) enabled.unshift(base); // the base currency is always enabled
  const locale = s(body.locale);
  if (!ALLOWED_LOCALES.includes(locale)) errors.locale = 'Choose a locale.';
  const timezone = s(body.timezone);
  if (!timezones().includes(timezone)) errors.timezone = 'Choose a timezone.';
  const firstDay = Number(s(body.firstDay));
  if (!Number.isInteger(firstDay) || firstDay < 1 || firstDay > 7) errors.firstDay = 'Choose the first day of the week.';
  const fiscalStart = Number(s(body.fiscalStart));
  if (!Number.isInteger(fiscalStart) || fiscalStart < 1 || fiscalStart > 12) errors.fiscalStart = 'Choose a month.';
  return { values: { base, enabled, locale, timezone, firstDay, fiscalStart }, errors };
}

export function validateFx(body: Record<string, unknown>, o: { base: string; enabled: string[]; locale: string }): Result<{ currency: string; month: string; rate: number }> {
  const errors: Errors = {};
  const currency = s(body.currency).toUpperCase();
  if (!currency || currency === o.base || !o.enabled.includes(currency)) errors.currency = 'Choose a currency.';
  const month = s(body.month);
  if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(month) || Number(month.slice(0, 4)) < 2000) errors.month = 'Choose a month.';
  let rate = 0;
  const raw = s(body.rate);
  const r = raw ? parseRate(raw, o.locale) : null;
  if (!raw || (r && !r.ok && r.reason === 'format')) errors.rate = `Enter a rate, for example ${rateExample(o.locale)}.`;
  else if (r && r.ok) {
    rate = r.value;
    if (rate <= 0) errors.rate = 'Rate must be more than 0.';
    else if (rate >= 1e10) errors.rate = `Enter a rate, for example ${rateExample(o.locale)}.`;
  } else errors.rate = 'Rate must be more than 0.';
  return { values: { currency, month, rate }, errors };
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
export function validateTeam(body: Record<string, unknown>): Result<{ name: string; groupId: string }> {
  const errors: Errors = {};
  const name = s(body.name);
  if (!name) errors.name = 'Enter a team name.';
  else if (name.length > 80) errors.name = 'Team name must be 80 characters or fewer.';
  const groupId = s(body.groupId).replace(/[{}\s]/g, '').toLowerCase();
  if (!groupId) errors.groupId = 'Enter the Entra group object ID.';
  else if (!GUID.test(groupId)) errors.groupId = 'Enter the group object ID in this format: 00000000-0000-0000-0000-000000000000.';
  return { values: { name, groupId }, errors };
}

export const validateLabel = (v: unknown): string | null => {
  const t = s(v);
  if (!t) return 'Enter a name.';
  if (t.length > 80) return 'Name must be 80 characters or fewer.';
  return null;
};

export interface StageRowInput { id: number | null; name: string; probability: number; type: 'open' | 'won' | 'lost' }
/** Rows come from the editor form. Won is forced to 100 and Lost to 0 (the database CHECKs require it). */
export function validateStages(rows: Array<{ id: string; name: string; prob: string; type: string }>): { rows: StageRowInput[]; errors: Record<string, string>; summary: string[] } {
  const errors: Record<string, string> = {};
  const summary: string[] = [];
  const out: StageRowInput[] = rows.map((r) => ({
    id: /^[1-9]\d{0,9}$/.test(r.id) ? Number(r.id) : null,
    name: s(r.name),
    probability: /^\d{1,3}$/.test(s(r.prob)) ? Number(s(r.prob)) : -1,
    type: r.type === 'won' || r.type === 'lost' ? r.type : 'open',
  }));
  const seen = new Set<string>();
  out.forEach((r, i) => {
    if (!r.name) errors[`name${i}`] = 'Enter a stage name.';
    else if (r.name.length > 60) errors[`name${i}`] = 'Stage name must be 60 characters or fewer.';
    else if (seen.has(r.name.toLowerCase())) { errors[`name${i}`] = 'Each stage needs a different name.'; }
    seen.add(r.name.toLowerCase());
    if (r.type === 'won') r.probability = 100;
    else if (r.type === 'lost') r.probability = 0;
    else if (r.probability < 0 || r.probability > 100) errors[`prob${i}`] = 'Probability must be between 0 and 100.';
  });
  const won = out.filter((r) => r.type === 'won').length;
  const lost = out.filter((r) => r.type === 'lost').length;
  if (won !== 1 || lost !== 1) summary.push('You need exactly one Won stage and one Lost stage.');
  if (!out.some((r) => r.type === 'open')) summary.push('You need at least one open stage.');
  return { rows: out, errors, summary };
}
