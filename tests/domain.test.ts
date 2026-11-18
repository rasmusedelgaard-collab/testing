import { describe, expect, it } from 'vitest';
import { parseListQuery, ENTITIES } from '../src/domain/entities';
import type { Lookups } from '../src/domain/types';
import { validateEntity } from '../src/domain/validators';
import { createRow, deleteRow, updateRows, auditText } from '../src/services/audit';
import { isRealDate, makeFormatter, parseAmount, todayIn } from '../src/services/format';

const lk = (locale = 'da-DK'): Lookups => ({
  settings: { appName: 'CRM', localeCode: locale, timezone: 'Europe/Copenhagen', baseCurrency: 'DKK', enabledCurrencies: ['DKK', 'EUR'], visibilityMode: 'private' },
  stages: [{ id: 1, name: 'Qual', sortOrder: 1, probability: 10, type: 'open' }, { id: 6, name: 'Lost', sortOrder: 6, probability: 0, type: 'lost' }],
  picklists: { industry: [{ id: 100, code: null, label: 'Tech', active: true }, { id: 101, code: null, label: 'Old', active: false }], lost_reason: [{ id: 300, code: null, label: 'Price', active: true }], lead_source: [] },
});

describe('validators', () => {
  it('account: required name, lengths, website/phone patterns, country upper-case, inactive picklist rejected', () => {
    expect(validateEntity('accounts', { OwnerId: '1' }, lk()).errors.Name).toBe('Enter an account name.');
    expect(validateEntity('accounts', { Name: 'x'.repeat(121), OwnerId: '1' }, lk()).errors.Name).toBe('Name must be 120 characters or fewer.');
    expect(validateEntity('accounts', { Name: 'a', Website: 'nope', OwnerId: '1' }, lk()).errors.Website).toContain('web address');
    expect(validateEntity('accounts', { Name: 'a', Phone: 'abc', OwnerId: '1' }, lk()).errors.Phone).toBe('Enter a valid phone number.');
    expect(validateEntity('accounts', { Name: 'a', IndustryValueId: '101', OwnerId: '1' }, lk()).errors.IndustryValueId).toBeDefined();
    const ok = validateEntity('accounts', { Name: ' Acme ', Website: 'acme.dk', CountryCode: 'dk', IndustryValueId: '100', OwnerId: '2' }, lk());
    expect(ok.errors).toEqual({});
    expect(ok.values).toMatchObject({ Name: 'Acme', CountryCode: 'DK', IndustryValueId: 100, OwnerId: 2, City: null });
  });
  it('contact: last name required, email format', () => {
    expect(validateEntity('contacts', { OwnerId: '1' }, lk()).errors.LastName).toBe('Enter a last name.');
    expect(validateEntity('contacts', { LastName: 'x', Email: 'bad', OwnerId: '1' }, lk()).errors.Email).toContain('name@example.com');
  });
  it('opportunity: amount in locale, currency, close date, lost reason', () => {
    const base = { Name: 'o', AccountId: '1', StageId: '1', Amount: '1.234,50', CurrencyCode: 'eur', CloseDate: '2026-12-31', OwnerId: '1' };
    expect(validateEntity('opportunities', base, lk()).values).toMatchObject({ Amount: 1234.5, CurrencyCode: 'EUR' });
    expect(validateEntity('opportunities', { ...base, Amount: '1,234.50' }, lk('en-GB')).values.Amount).toBe(1234.5);
    expect(validateEntity('opportunities', { ...base, StageId: '99' }, lk()).errors.StageId).toBeDefined();
    expect(validateEntity('opportunities', { ...base, StageId: '6' }, lk()).errors.LostReasonValueId).toBe('Choose why this was lost.');
    expect(validateEntity('opportunities', { ...base, StageId: '1', LostReasonValueId: '300' }, lk()).values.LostReasonValueId).toBeNull();
    expect(validateEntity('opportunities', { ...base, Name: '', AccountId: '' }, lk()).errors).toMatchObject({ Name: 'Enter an opportunity name.', AccountId: 'Choose an account.' });
  });
  it('rejects non-string and prototype-pollution style input without throwing', () => {
    const r = validateEntity('accounts', { Name: ['a'], OwnerId: { x: 1 }, __proto__: { a: 1 } } as never, lk());
    expect(Object.keys(r.errors).length).toBeGreaterThan(0);
  });
  it('activity: type enum, subject, links, note clears dates', () => {
    expect(validateEntity('activities', { ActivityType: 'x', Subject: '', OwnerId: '1' }, lk()).errors).toMatchObject({ ActivityType: 'Choose a type.', Subject: 'Enter a subject.' });
    const n = validateEntity('activities', { ActivityType: 'note', Subject: 's', AccountId: '1', DueDate: '2026-01-01', OwnerId: '1' }, lk());
    expect(n.values).toMatchObject({ DueDate: null, DueTime: null, IsDone: false });
    expect(validateEntity('activities', { ActivityType: 'call', Subject: 's', AccountId: '1', DueDate: '2026-02-30', OwnerId: '1' }, lk()).errors.DueDate).toContain('real date');
    expect(validateEntity('activities', { ActivityType: 'call', Subject: 's', ContactId: '3', IsDone: 'on', OwnerId: '1' }, lk()).values.IsDone).toBe(true);
  });
});

describe('format', () => {
  it('formats money, dates and date-times per locale and timezone', () => {
    const da = makeFormatter({ localeCode: 'da-DK', timezone: 'Europe/Copenhagen' });
    expect(da.money(12500, 'DKK')).toBe('DKK 12.500,00');
    expect(da.date('2026-12-31')).toBe('31.12.2026');
    expect(da.dateTime('2026-11-18T13:02:00.000Z')).toMatch(/18\.11\.2026,? 14[.:]02/);
    const gb = makeFormatter({ localeCode: 'en-GB', timezone: 'UTC' });
    expect(gb.money(12500, 'EUR')).toBe('EUR 12,500.00');
    expect(gb.date('2026-12-31')).toBe('31/12/2026');
    expect(gb.date('')).toBe('');
  });
  it('falls back safely for unknown locale/timezone', () => {
    expect(makeFormatter({ localeCode: 'xx', timezone: 'Nope/Zone' }).date('2026-01-02')).toBe('02/01/2026');
  });
  it('parseAmount handles locale separators and rejects junk', () => {
    expect(parseAmount('12.500,00', 'da-DK')).toEqual({ ok: true, value: 12500 });
    expect(parseAmount('12500', 'da-DK')).toEqual({ ok: true, value: 12500 });
    expect(parseAmount('12,500.5', 'en-GB')).toEqual({ ok: true, value: 12500.5 });
    expect(parseAmount('-1', 'da-DK')).toEqual({ ok: false, reason: 'negative' });
    for (const bad of ['', 'abc', '1,234', '1,234,567,89', '1,2,3']) expect(parseAmount(bad, 'da-DK').ok, bad).toBe(false);
  });
  it('isRealDate and todayIn', () => {
    expect(isRealDate('2028-02-29')).toBe(true);
    expect(isRealDate('2027-02-29')).toBe(false);
    expect(isRealDate('31.12.2026')).toBe(false);
    expect(todayIn('Pacific/Auckland', new Date('2026-11-18T12:00:00Z'))).toBe('2026-11-19');
  });
});

describe('audit builders', () => {
  it('create and delete summary rows carry the record label and owner', () => {
    expect(createRow('accounts', 7, 'Acme', 3, 1)).toMatchObject({ action: 'C', entityType: 'account', entityId: 7, newValue: 'Acme', ownerId: 3, teamId: 1 });
    expect(deleteRow('opportunities', 9, 'Deal', 4, null)).toMatchObject({ action: 'D', oldValue: 'Deal', teamId: null });
  });
  it('update rows only for changed audited fields, in invariant text and truncated', () => {
    const rows = updateRows('opportunities', 1, { Amount: 10.5, StageId: 1, CurrencyCode: 'DKK', CloseDate: '2026-01-01', OwnerId: 3, Name: 'a' }, { Amount: 11, StageId: 1, CurrencyCode: 'EUR', CloseDate: '2026-01-01', OwnerId: 3, Name: 'b' }, 3, 1);
    expect(rows.map((r) => [r.fieldName, r.oldValue, r.newValue])).toEqual([['Amount', '10.5', '11'], ['CurrencyCode', 'DKK', 'EUR']]);
    expect(updateRows('accounts', 1, { OwnerId: 1, Name: 'a' }, { OwnerId: 1, Name: 'b' }, 1, null)).toEqual([]);
    expect(auditText('x'.repeat(5000))).toHaveLength(4000);
    expect(auditText(true)).toBe('1');
    expect(auditText(null)).toBeNull();
  });
});

describe('parseListQuery', () => {
  const def = ENTITIES.opportunities;
  it('applies defaults and whitelists', () => {
    const q = parseListQuery(def, {}, '2026-11-18');
    expect(q).toMatchObject({ page: 1, pageSize: 25, sort: 'close', dir: 'asc', filters: { status: 'open' } });
    const q2 = parseListQuery(def, { sort: 'x', dir: 'desc', status: 'bogus', stage: '3', from: '2026-01-01', to: 'nope', pageSize: '50', page: '2', q: ' hi ' }, 't');
    expect(q2).toMatchObject({ sort: 'close', dir: 'desc', pageSize: 50, page: 2, q: 'hi', filters: { status: 'open', stage: '3', from: '2026-01-01' } });
    expect(q2.filters.to).toBeUndefined();
  });
});
