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
