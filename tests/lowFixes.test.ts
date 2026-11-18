import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { loadConfig } from '../src/config';
import { ENTITIES, parseListQuery } from '../src/domain/entities';
import { validateEntity } from '../src/domain/validators';
import { createMemoryStore } from '../src/db/memoryStore';
import { demoConfig, makeApp } from './helpers';

describe('N1: prototype keys never reach sorting', () => {
  it.each(['constructor', '__proto__', 'toString', 'hasOwnProperty', 'valueOf'])('sort=%s falls back to the default', (k) => {
    expect(parseListQuery(ENTITIES.accounts, { sort: k }, '2026-01-01').sort).toBe('name');
  });
  it('pages answer 200 for prototype sort keys', async () => {
    const { app } = makeApp({ config: demoConfig(), store: createMemoryStore() });
    const agent = request.agent(app);
    await agent.get('/__test/login?role=Admin');
    for (const e of ['accounts', 'contacts', 'opportunities', 'activities']) {
      for (const k of ['constructor', '__proto__', 'toString']) expect((await agent.get(`/${e}?sort=${k}`)).status, `${e} ${k}`).toBe(200);
    }
  });
});

describe('N2: integer values above the SQL int range', () => {
  it('filters above 2147483647 are dropped', () => {
    expect(parseListQuery(ENTITIES.accounts, { owner: '2147483648' }, 't').filters.owner).toBeUndefined();
    expect(parseListQuery(ENTITIES.accounts, { owner: '2147483647' }, 't').filters.owner).toBe('2147483647');
  });
  it('form ids above the range are validation errors, not 500s', () => {
    const lk = { settings: { appName: 'CRM', localeCode: 'en-GB', timezone: 'UTC', baseCurrency: 'DKK', enabledCurrencies: ['DKK'], visibilityMode: 'private', accentColor: '#1D4ED8', firstDayOfWeek: 1, fiscalStartMonth: 1 }, stages: [], picklists: {} } as never;
    const r = validateEntity('contacts', { LastName: 'x', AccountId: '2147483648', OwnerId: '99999999999' }, lk);
    expect(r.errors.AccountId).toBeDefined();
    expect(r.errors.OwnerId).toBeDefined();
  });
  it('HTTP: huge ids in the URL and filters answer 404/200', async () => {
    const { app } = makeApp({ config: demoConfig(), store: createMemoryStore() });
    const agent = request.agent(app);
    await agent.get('/__test/login?role=Admin');
    expect((await agent.get('/accounts?owner=99999999999')).status).toBe(200);
    expect((await agent.get('/accounts/99999999999')).status).toBe(404);
  });
});

describe('N3: NODE_ENV=test is refused next to a real environment', () => {
  it.each([{ DB_SERVER: 'x' }, { AUTH_MODE: 'test-access-code' }, { LIVE: 'true' }, { WEBSITE_INSTANCE_ID: 'x' }, { WEBSITE_SITE_NAME: 'x' }])('refused with %j', (extra) => {
    expect(() => loadConfig({ NODE_ENV: 'test', ...extra })).toThrow(/NODE_ENV=test \(auth bypass\) is refused/);
  });
  it('plain NODE_ENV=test still works', () => {
    expect(loadConfig({ NODE_ENV: 'test' }).demoMode).toBe(true);
  });
});

describe('QA-I2 fixes', () => {
  it('I2-3: /readyz in demo mode answers 200 with a JSON body', async () => {
    const { app } = makeApp({ config: demoConfig(), store: createMemoryStore() });
    const res = await request(app).get('/readyz');
    expect(res.status).toBe(200);
    expect(res.body).toEqual({ status: 'ok', db: 'demo' });
  });
  it('I2-1: the error names the problem (inactive owner) on the owner field', async () => {
    const store = createMemoryStore();
    store.state.settings.visibilityMode = 'team';
    store.state.users.find((u) => u.id === 4)!.active = false;
    const { app } = makeApp({ config: demoConfig(), store });
    const agent = request.agent(app);
    await agent.get('/__test/login?role=User');
    const page = await agent.get('/accounts/2/edit');
    const rowVer = /name="rowVer" value="([^"]+)"/.exec(page.text)![1]!;
    const csrf = /name="_csrf" value="([^"]+)"/.exec(page.text)![1]!;
    const res = await agent.post('/accounts/2').type('form').send({ _csrf: csrf, rowVer, Name: 'Edited', OwnerId: '4' });
    expect(res.status).toBe(422);
    expect(res.text).toContain('Current owner is inactive: choose a new owner.');
    expect(res.text).toContain('value="Edited"');
  });
});

describe('QA-I3-2/3: unchanged legacy values stay valid, new selections must be current', () => {
  const lk = { settings: { appName: 'CRM', localeCode: 'en-GB', timezone: 'UTC', baseCurrency: 'DKK', enabledCurrencies: ['DKK'], visibilityMode: 'private', accentColor: '#1D4ED8', firstDayOfWeek: 1, fiscalStartMonth: 1 }, stages: [{ id: 1, name: 'Q', sortOrder: 1, probability: 10, type: 'open' }], picklists: { industry: [{ id: 5, code: null, label: 'Old', active: false }], lead_source: [], lost_reason: [] } } as never;
  const opp = { Name: 'o', AccountId: '1', StageId: '1', Amount: '1', CurrencyCode: 'EUR', CloseDate: '2026-12-01', OwnerId: '1' };
  it('a disabled currency is accepted only when it is the record\'s current one', async () => {
    expect(validateEntity('opportunities', opp, lk, { CurrencyCode: 'EUR' }).errors.CurrencyCode).toBeUndefined();
    expect(validateEntity('opportunities', opp, lk, { CurrencyCode: 'DKK' }).errors.CurrencyCode).toBe('Choose a currency.');
    expect(validateEntity('opportunities', opp, lk, null).errors.CurrencyCode).toBe('Choose a currency.');
  });
  it('an inactive picklist value is accepted only when unchanged', async () => {
    const acc = { Name: 'a', OwnerId: '1', IndustryValueId: '5' };
    expect(validateEntity('accounts', acc, lk, { IndustryValueId: 5 }).errors.IndustryValueId).toBeUndefined();
    expect(validateEntity('accounts', acc, lk, { IndustryValueId: 6 }).errors.IndustryValueId).toBeDefined();
    expect(validateEntity('accounts', acc, lk, null).errors.IndustryValueId).toBeDefined();
  });
});
