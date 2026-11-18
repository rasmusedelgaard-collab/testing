import { describe, expect, it } from 'vitest';
import { amountExample, parseAmount, parseRate, rateExample } from '../src/services/format';
import { validateFx } from '../src/domain/settingsValidators';
import { validateEntity } from '../src/domain/validators';
import type { Lookups } from '../src/domain/types';

const ok = (value: number) => ({ ok: true, value });
const bad = { ok: false, reason: 'format' };

/*
 * Rules (src/services/format.ts parseDecimal): locale decimal separator + 1..maxDecimals digits; integer part is plain digits OR
 * properly grouped (1-3 digits, then groups of exactly 3). The OTHER locale's separator never disappears silently.
 */
describe('parseAmount: da-DK (group ".", decimal ",")', () => {
  it.each([
    ['1.234,56', 1234.56], ['1234,56', 1234.56], ['6,8', 6.8], ['7,4590', null], ['0,5', 0.5], ['12500', 12500], ['12.500', 12500], ['1.234.567,89', 1234567.89],
    ['0', 0], ['007', 7], ['100,00', 100], ['999.999.999,99', 999999999.99], ['12,5', 12.5], ['1.000', 1000],
  ])('accepts %s', (input, value) => {
    expect(parseAmount(input, 'da-DK')).toEqual(value === null ? bad : ok(value));
  });
  it.each([
    '12.5', '1.23', '1.2345', '12.34.56', '1.234.56', '1234.567', '1,234,56', '1,2,3', '1.234,5678', '1,234', '1.', '.5', ',5', '5,', '1 234', '1 234,56', '', ' ', 'abc', '1e5', '0x10', '1,2.3', '1..234', '12.500,0,0', '+5', '١٢٣',
  ])('rejects %s', (input) => {
    expect(parseAmount(input, 'da-DK').ok).toBe(false);
  });
  it('the dangerous misreads are errors, never other numbers', () => {
    expect(parseAmount('12.5', 'da-DK').ok).toBe(false); // would have been 125
    expect(parseAmount('6.8', 'da-DK').ok).toBe(false); // would have been 68
    expect(parseAmount('7.4590', 'da-DK').ok).toBe(false); // would have been 74590
  });
});

describe('parseAmount: en-GB and en-US (group ",", decimal ".")', () => {
  it.each(['en-GB', 'en-US'])('%s accepts the right forms', (locale) => {
    for (const [input, value] of [['1,234.56', 1234.56], ['1234.56', 1234.56], ['6.8', 6.8], ['12,500', 12500], ['12500', 12500], ['1,234,567.89', 1234567.89], ['0.5', 0.5], ['100', 100], ['1,000', 1000]] as const) {
      expect(parseAmount(input, locale), input).toEqual(ok(value));
    }
  });
  it.each(['en-GB', 'en-US'])('%s rejects look-alikes', (locale) => {
    for (const input of ['6,8', '1,23', '1,2345', '12,34,56', '1234,567', '1,234.567', '1.234,56', '12.5.6', '1,23,456', ',5', '.', '', '1 234.56', '1,,234']) {
      expect(parseAmount(input, locale).ok, input).toBe(false);
    }
  });
  it('"6,8" is an error, not 68 (H3)', () => {
    expect(parseAmount('6,8', 'en-GB').ok).toBe(false);
  });
});

describe('negatives and limits', () => {
  it('a leading minus is reported as negative', () => {
    expect(parseAmount('-5', 'da-DK')).toEqual({ ok: false, reason: 'negative' });
    expect(parseAmount('-1.234,00', 'da-DK')).toEqual({ ok: false, reason: 'negative' });
    expect(parseAmount(' -5', 'en-GB')).toEqual({ ok: false, reason: 'negative' });
  });
  it('absurdly large values are rejected', () => {
    expect(parseAmount('9'.repeat(30), 'en-GB').ok).toBe(false);
  });
  it('unknown locales fall back to en-GB rules', () => {
    expect(parseAmount('1,234.5', 'xx')).toEqual(ok(1234.5));
  });
});

describe('parseRate: up to 6 decimals, no group separators at all', () => {
  it.each([['7,4590', 7.459], ['0,1', 0.1], ['7,123456', 7.123456], ['10', 10], ['1000,5', 1000.5]])('da-DK accepts %s', (input, v) => {
    expect(parseRate(input, 'da-DK')).toEqual(ok(v));
  });
  it.each(['7.4590', '1.234,5', '7,1234567', '7,', ',5', '1.000', '7 459', ''])('da-DK rejects %s', (input) => {
    expect(parseRate(input, 'da-DK').ok, input).toBe(false);
  });
  it.each([['7.4590', 7.459], ['0.1', 0.1], ['1000.5', 1000.5]])('en-GB accepts %s', (input, v) => {
    expect(parseRate(input, 'en-GB')).toEqual(ok(v));
  });
  it.each(['6,8', '7,4590', '1,000.5', '7.1234567', '1,000'])('en-GB rejects %s', (input) => {
    expect(parseRate(input, 'en-GB').ok, input).toBe(false);
  });
  it('0 parses (the "more than 0" rule is a separate message); negatives are flagged', () => {
    expect(parseRate('0', 'da-DK')).toEqual(ok(0));
    expect(parseRate('-1', 'da-DK')).toEqual({ ok: false, reason: 'negative' });
  });
});

describe('examples in messages follow the locale', () => {
  it('amount and rate examples', () => {
    expect(amountExample('da-DK')).toBe('12.500,00');
    expect(amountExample('en-GB')).toBe('12,500.00');
    expect(rateExample('da-DK')).toBe('7,4590');
    expect(rateExample('en-GB')).toBe('7.4590');
    expect(rateExample('en-US')).toBe('7.4590');
  });
  it('the rate error uses the locale example, and bad separators are errors in the validator too', () => {
    const o = (locale: string) => ({ base: 'DKK', enabled: ['DKK', 'EUR'], locale });
    expect(validateFx({ currency: 'EUR', month: '2026-11', rate: '7.4590' }, o('da-DK')).errors.rate).toBe('Enter a rate, for example 7,4590.');
    expect(validateFx({ currency: 'EUR', month: '2026-11', rate: '6,8' }, o('en-GB')).errors.rate).toBe('Enter a rate, for example 7.4590.');
    expect(validateFx({ currency: 'EUR', month: '2026-11', rate: '7,4590' }, o('da-DK')).values.rate).toBe(7.459);
    expect(validateFx({ currency: 'EUR', month: '2026-11', rate: '0' }, o('da-DK')).errors.rate).toBe('Rate must be more than 0.');
    expect(validateFx({ currency: 'EUR', month: '2026-11', rate: '-2' }, o('da-DK')).errors.rate).toBe('Rate must be more than 0.');
  });
  it('the opportunity amount error uses the locale example and never saves a misread value', () => {
    const lk = (locale: string) => ({ settings: { appName: 'CRM', localeCode: locale, timezone: 'UTC', baseCurrency: 'DKK', enabledCurrencies: ['DKK'], visibilityMode: 'private', accentColor: '#1D4ED8', firstDayOfWeek: 1, fiscalStartMonth: 1 }, stages: [{ id: 1, name: 'Q', sortOrder: 1, probability: 10, type: 'open' }], picklists: {} }) as unknown as Lookups;
    const body = (amount: string) => ({ Name: 'o', AccountId: '1', StageId: '1', Amount: amount, CurrencyCode: 'DKK', CloseDate: '2026-12-01', OwnerId: '1' });
    expect(validateEntity('opportunities', body('6,8'), lk('en-GB')).errors.Amount).toBe('Enter an amount, for example 12,500.00.');
    expect(validateEntity('opportunities', body('12.5'), lk('da-DK')).errors.Amount).toBe('Enter an amount, for example 12.500,00.');
    expect(validateEntity('opportunities', body('1.234,56'), lk('da-DK')).values.Amount).toBe(1234.56);
    expect(validateEntity('opportunities', body('1,234.56'), lk('en-GB')).values.Amount).toBe(1234.56);
  });
});
