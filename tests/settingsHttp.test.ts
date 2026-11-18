import request from 'supertest';
import { describe, expect, it } from 'vitest';
import { createMemoryStore } from '../src/db/memoryStore';
import { checkLogo, svgIsSafe } from '../src/services/logo';
import { contrastWithWhite } from '../src/services/color';
import { validateFx, validateStages, validateTeam } from '../src/domain/settingsValidators';
import { csrfFrom, demoConfig, makeApp } from './helpers';

const PNG = Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), Buffer.alloc(64, 1)]);
const SVG = Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><rect width="10" height="10" fill="#123456"/></svg>');

async function session(role: 'Admin' | 'Manager' | 'User' = 'Admin') {
  const store = createMemoryStore();
  const { app } = makeApp({ config: demoConfig(), store });
  const agent = request.agent(app);
  await agent.get(`/__test/login?role=${role}`);
  const token = async () => csrfFrom((await agent.get('/')).text);
  const post = async (path: string, body: Record<string, unknown>) => agent.post(path).type('form').send({ _csrf: await token(), ...body });
  return { agent, app, store, state: store.state, post, token };
}
const audit = (s: { state: { audit: Array<{ entityType: string; action: string; fieldName: string | null; oldValue: string | null; newValue: string | null }> } }, type: string) => s.state.audit.filter((a) => a.entityType === type);

describe('access control', () => {
  const paths = ['/settings', '/settings/general', '/settings/locale', '/settings/fx', '/settings/stages', '/settings/picklists/industry', '/settings/visibility', '/settings/users', '/settings/teams/1/edit', '/settings/fx/1/edit'];
  it.each(['Manager', 'User'] as const)('%s gets the 403 page on every settings GET and POST', async (role) => {
    const s = await session(role);
    for (const p of paths) expect((await s.agent.get(p)).status, p).toBe(403);
    for (const p of ['/settings/general', '/settings/locale', '/settings/fx', '/settings/stages', '/settings/visibility', '/settings/teams', '/settings/picklists/industry']) {
      expect((await s.post(p, { appName: 'x' })).status, p).toBe(403);
    }
    expect(s.state.settings.appName).toBe('CRM');
  });
  it('anonymous users are redirected to sign-in', async () => {
    const { app } = makeApp({ config: demoConfig() });
    expect((await request(app).get('/settings/general')).status).toBe(303);
  });
  it('Admin can open every page', async () => {
    const s = await session();
    for (const p of paths.filter((x) => !x.includes('/teams/1') && !x.includes('/fx/1') && x !== '/settings')) expect((await s.agent.get(p)).status, p).toBe(200);
    expect((await s.agent.get('/settings/teams/1/edit')).status).toBe(200);
    expect((await s.agent.get('/settings/picklists/nope')).status).toBe(404);
  });
  it('the settings sub-nav marks the current page', async () => {
    const s = await session();
    expect((await s.agent.get('/settings/fx')).text).toMatch(/href="\/settings\/fx" aria-current="page"/);
  });
});

describe('general settings and branding', () => {
  it('saves name and accent, audits the changes, and applies them immediately everywhere', async () => {
    const s = await session();
    const res = await s.post('/settings/general', { appName: 'Acme CRM', accentColor: '#0b5394' });
    expect(res.status).toBe(303);
    expect(audit(s, 'setting').map((a) => [a.fieldName, a.oldValue, a.newValue])).toEqual([['app.name', 'CRM', 'Acme CRM'], ['branding.accentColor', '#1D4ED8', '#0B5394']]);
    expect((await s.agent.get('/settings/general')).text).toContain('Settings saved.');
    expect((await s.agent.get('/')).text).toContain('Acme CRM');
    const css = await s.agent.get('/css/theme.css');
    expect(css.text).toContain('--color-primary: #0B5394');
    expect(css.headers['cache-control']).toBe('no-cache');
    expect((await s.agent.get('/css/theme.css').set('If-None-Match', css.headers.etag as string)).status).toBe(304);
  });
  it('the public sign-in page also shows the new name', async () => {
    const s = await session();
    await s.post('/settings/general', { appName: 'Acme CRM', accentColor: '#1D4ED8' });
    expect((await request(s.app).get('/signin')).text).toContain('Acme CRM');
  });
  it('refuses a colour below 4.5:1 contrast, bad formats, and empty or long names, keeping input', async () => {
    const s = await session();
    const light = await s.post('/settings/general', { appName: 'X', accentColor: '#FFEB3B' });
    expect(light.status).toBe(422);
    expect(light.text).toContain('This colour is too light for text and buttons. Choose a darker shade.');
    expect(light.text).toContain('value="#FFEB3B"');
    expect((await s.post('/settings/general', { appName: 'X', accentColor: 'red' })).text).toContain('Enter a colour like #1D4ED8.');
    expect((await s.post('/settings/general', { appName: '', accentColor: '#1D4ED8' })).text).toContain('Enter a CRM name.');
    expect((await s.post('/settings/general', { appName: 'x'.repeat(41), accentColor: '#1D4ED8' })).text).toContain('40 characters or fewer');
    expect(s.state.settings.appName).toBe('CRM');
  });
  it('contrast maths', () => {
    expect(contrastWithWhite('#FFFFFF')).toBeCloseTo(1, 1);
    expect(contrastWithWhite('#000000')).toBeCloseTo(21, 0);
    expect(contrastWithWhite('#1D4ED8')).toBeGreaterThan(4.5);
    expect(contrastWithWhite('nope')).toBe(0);
  });
});

describe('logo upload', () => {
  const upload = async (s: Awaited<ReturnType<typeof session>>, file: Buffer, name = 'logo.png', type = 'image/png', csrf?: string) => {
    const token = csrf ?? (await s.token());
    const req = s.agent.post('/settings/general/logo');
    if (token) req.field('_csrf', token);
    return req.attach('logo', file, { filename: name, contentType: type });
  };
  it('accepts a PNG, stores it, audits it and serves it with hardened headers and an ETag', async () => {
    const s = await session();
    expect((await upload(s, PNG)).status).toBe(303);
    expect(s.state.logo?.contentType).toBe('image/png');
    expect(audit(s, 'branding')).toHaveLength(1);
    const res = await request(s.app).get('/branding/logo');
    expect(res.status).toBe(200);
    expect(res.headers['content-type']).toContain('image/png');
    expect(res.headers['x-content-type-options']).toBe('nosniff');
    expect(res.headers['content-security-policy']).toBe("default-src 'none'; style-src 'unsafe-inline'; sandbox");
    expect(res.headers['cache-control']).toBe('no-cache');
    expect((await request(s.app).get('/branding/logo').set('If-None-Match', res.headers.etag as string)).status).toBe(304);
    expect((await s.agent.get('/')).text).toContain('<img class="logo" src="/branding/logo?v=');
  });
  it('accepts a clean SVG', async () => {
    const s = await session();
    await upload(s, SVG, 'logo.svg', 'image/svg+xml');
    expect(s.state.logo?.contentType).toBe('image/svg+xml');
  });
  it.each([
    ['script element', '<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>'],
    ['event handler', '<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"></svg>'],
    ['javascript url', '<svg xmlns="http://www.w3.org/2000/svg"><a href="javascript:alert(1)"><rect/></a></svg>'],
    ['foreignObject', '<svg xmlns="http://www.w3.org/2000/svg"><foreignObject><div/></foreignObject></svg>'],
    ['external href', '<svg xmlns="http://www.w3.org/2000/svg"><image href="https://evil.example/x.png"/></svg>'],
    ['external xlink', '<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><use xlink:href="http://evil.example/a.svg#x"/></svg>'],
    ['css url()', '<svg xmlns="http://www.w3.org/2000/svg"><rect style="fill:url(https://evil.example/x)"/></svg>'],
    ['doctype/entity', '<!DOCTYPE svg [<!ENTITY x SYSTEM "file:///etc/passwd">]><svg xmlns="http://www.w3.org/2000/svg">&x;</svg>'],
    ['mixed case', '<svg xmlns="http://www.w3.org/2000/svg"><SCRIPT>1</SCRIPT></svg>'],
  ])('rejects an SVG with %s', async (_n, svg) => {
    const s = await session();
    const res = await upload(s, Buffer.from(svg), 'x.svg', 'image/svg+xml');
    expect(res.status).toBe(303);
    expect(s.state.logo).toBeNull();
    expect((await s.agent.get('/settings/general')).text).toMatch(/This SVG contains script or external content|Logo must be PNG or SVG/);
  });
  it('decides by content, not by filename or declared type', async () => {
    const s = await session();
    await upload(s, Buffer.from('<?php echo 1;'), 'logo.png', 'image/png');
    await upload(s, Buffer.from('GIF89a......'), 'logo.svg', 'image/svg+xml');
    await upload(s, Buffer.from('<html><body>hi</body></html>'), 'a.svg', 'image/svg+xml');
    expect(s.state.logo).toBeNull();
    expect(checkLogo(PNG).ok).toBe(true);
    expect(checkLogo(Buffer.alloc(0)).ok).toBe(false);
  });
  it('rejects files over 500 KB without storing them', async () => {
    const s = await session();
    const big = Buffer.concat([PNG, Buffer.alloc(520_000)]);
    const res = await upload(s, big);
    expect(res.status).toBe(303);
    expect(s.state.logo).toBeNull();
    expect((await s.agent.get('/settings/general')).text).toContain('Logo must be PNG or SVG, 500 KB or smaller.');
  });
  it('requires the CSRF token (checked after multipart parsing) and an Admin', async () => {
    const s = await session();
    expect((await upload(s, PNG, 'logo.png', 'image/png', '')).status).toBe(403);
    expect((await upload(s, PNG, 'logo.png', 'image/png', 'forged')).status).toBe(403);
    expect(s.state.logo).toBeNull();
    const u = await session('User');
    expect((await upload(u, PNG)).status).toBe(403);
    expect(u.state.logo).toBeNull();
  });
  it('other routes never accept multipart bodies', async () => {
    const s = await session();
    const res = await s.agent.post('/settings/general').field('_csrf', await s.token()).field('appName', 'Hacked').field('accentColor', '#1D4ED8');
    expect(res.status).toBe(403);
    expect(s.state.settings.appName).toBe('CRM');
  });
  it('/branding/logo is 404 when no logo exists', async () => {
    const s = await session();
    expect((await request(s.app).get('/branding/logo')).status).toBe(404);
  });
  it('svgIsSafe allows internal fragment references', () => {
    expect(svgIsSafe('<svg><defs><linearGradient id="g"/></defs><rect fill="url(#g)"/><use href="#g"/></svg>')).toBe(true);
  });
});

describe('currency and locale', () => {
  const form = (over: Record<string, unknown> = {}) => ({ base: 'DKK', enabled: ['DKK', 'EUR'], locale: 'en-GB', timezone: 'Europe/Copenhagen', firstDay: '1', fiscalStart: '4', ...over });
  it('saves, audits, and changes formatting immediately', async () => {
    const s = await session();
    expect((await s.post('/settings/locale', form())).status).toBe(303);
    expect(s.state.settings).toMatchObject({ localeCode: 'en-GB', enabledCurrencies: ['DKK', 'EUR'], fiscalStartMonth: 4 });
    expect(audit(s, 'setting').map((a) => a.fieldName)).toEqual(expect.arrayContaining(['locale.code', 'currency.enabled', 'fiscal.startMonth']));
    expect((await s.agent.get('/opportunities')).text).toContain('DKK 120,000.00');
    expect((await s.agent.get('/settings/locale')).text).toContain('Preview');
  });
  it('the base currency is always enabled; invalid values are refused', async () => {
    const s = await session();
    await s.post('/settings/locale', form({ base: 'SEK', enabled: ['EUR'] }));
    expect(s.state.settings.enabledCurrencies).toEqual(['SEK', 'EUR']);
    for (const bad of [{ base: 'XXX' }, { locale: 'fr-FR' }, { timezone: 'Mars/Base' }, { firstDay: '9' }, { fiscalStart: '13' }]) {
      expect((await s.post('/settings/locale', form(bad))).status, JSON.stringify(bad)).toBe(422);
    }
  });
});

describe('FX rates', () => {
  const rate = (over: Record<string, string> = {}) => ({ currency: 'EUR', month: '2026-11', rate: '7,4590', ...over });
  it('adds a rate (locale-parsed), lists it, audits it, and shows missing rows', async () => {
    const s = await session();
    expect((await s.agent.get('/settings/fx')).text).toContain('Missing rate for');
    expect((await s.post('/settings/fx', rate())).status).toBe(303);
    expect(s.state.fx[0]).toMatchObject({ currency: 'EUR', month: '2026-11', rate: 7.459, base: 'DKK' });
    expect(audit(s, 'fxrate')[0]).toMatchObject({ action: 'C' });
    const page = (await s.agent.get('/settings/fx?currency=EUR&year=2026')).text;
    expect(page).toContain('Rate saved.');
    expect(page).toContain('7,4590');
    expect(page).toContain('11.2026');
  });
  it('validates with the exact copy', async () => {
    const s = await session();
    expect((await s.post('/settings/fx', rate({ currency: '' }))).text).toContain('Choose a currency.');
    expect((await s.post('/settings/fx', rate({ currency: 'DKK' }))).text).toContain('Choose a currency.'); // base currency has an implicit rate of 1
    expect((await s.post('/settings/fx', rate({ month: '' }))).text).toContain('Choose a month.');
    expect((await s.post('/settings/fx', rate({ rate: 'abc' }))).text).toContain('Enter a rate, for example 7,4590.');
    expect((await s.post('/settings/fx', rate({ rate: '0' }))).text).toContain('Rate must be more than 0.');
    expect((await s.post('/settings/fx', rate({ rate: '-1' }))).text).toContain('Rate must be more than 0.');
    expect((await s.post('/settings/fx', rate({ rate: '1,1234567' }))).text).toContain('Enter a rate');
    expect(s.state.fx).toHaveLength(0);
  });
  it('rejects a duplicate currency+month and links to the existing rate', async () => {
    const s = await session();
    await s.post('/settings/fx', rate());
    const dup = await s.post('/settings/fx', rate({ rate: '8' }));
    expect(dup.status).toBe(422);
    expect(dup.text).toContain('A rate for EUR in 11.2026 already exists.');
    expect(dup.text).toContain(`/settings/fx/${s.state.fx[0]!.id}/edit`);
    expect(s.state.fx).toHaveLength(1);
  });
  it('edits (audited old and new) and deletes with a confirmation page', async () => {
    const s = await session();
    await s.post('/settings/fx', rate());
    const id = s.state.fx[0]!.id;
    expect((await s.agent.get(`/settings/fx/${id}/edit`)).text).toContain('value="7,459"');
    expect((await s.post(`/settings/fx/${id}`, rate({ rate: '7,5' }))).status).toBe(303);
    expect(audit(s, 'fxrate').at(-1)).toMatchObject({ action: 'U', fieldName: 'Rate', oldValue: '7.459', newValue: '7.5' });
    const confirm = await s.agent.get(`/settings/fx/${id}/delete`);
    expect(confirm.text).toContain('This changes totals on the dashboard and in analytics for 11.2026. Continue?');
    expect(s.state.fx).toHaveLength(1);
    expect((await s.post(`/settings/fx/${id}/delete`, {})).status).toBe(303);
    expect(s.state.fx).toHaveLength(0);
    expect(audit(s, 'fxrate').at(-1)).toMatchObject({ action: 'D' });
    expect((await s.post(`/settings/fx/${id}/delete`, {})).status).toBe(404);
  });
  it('validators', () => {
    const o = { base: 'DKK', enabled: ['DKK', 'EUR'], locale: 'da-DK' };
    expect(validateFx({ currency: 'eur', month: '2026-02', rate: '7,5' }, o).values).toEqual({ currency: 'EUR', month: '2026-02', rate: 7.5 });
    expect(validateFx({ currency: 'EUR', month: '2026-13', rate: '1' }, o).errors.month).toBeDefined();
  });
});

describe('pipeline stages', () => {
  const rowsOf = (s: Awaited<ReturnType<typeof session>>) => s.state.stages.map((st, i) => ({ [`id_${i}`]: String(st.id), [`name_${i}`]: st.name, [`prob_${i}`]: String(st.probability), [`type_${i}`]: st.type }));
  const stageForm = (s: Awaited<ReturnType<typeof session>>, over: Record<string, string> = {}) => ({ n: String(s.state.stages.length), ...Object.assign({}, ...rowsOf(s)), ...over });
  it('shows the stages with usage counts', async () => {
    const s = await session();
    const html = (await s.agent.get('/settings/stages')).text;
    expect(html).toContain('value="Qualification"');
    expect(html).toContain('Move Qualification down');
  });
  it('Move up/down and Add only re-render (nothing is saved until Save stages)', async () => {
    const s = await session();
    const res = await s.post('/settings/stages', { ...stageForm(s), action: 'down:0' });
    expect(res.status).toBe(200);
    expect(res.text).toContain('not saved yet');
    expect(s.state.stages[0]!.name).toBe('Qualification');
    expect((await s.post('/settings/stages', { ...stageForm(s), action: 'add' })).text).toContain('name="name_6"');
  });
  it('saves a new order, rename and probability, and audits each change', async () => {
    const s = await session();
    const f = stageForm(s, { name_0: 'Discovery', prob_0: '15' });
    const swapped = { ...f, id_0: f.id_1, name_0: f.name_1, prob_0: f.prob_1, type_0: f.type_1, id_1: String(s.state.stages[0]!.id), name_1: 'Discovery', prob_1: '15', type_1: 'open' };
    expect((await s.post('/settings/stages', { ...swapped, action: 'save' })).status).toBe(303);
    expect(s.state.stages.slice(0, 2).map((x) => [x.name, x.sortOrder])).toEqual([['Needs analysis', 1], ['Discovery', 2]]);
    const fields = audit(s, 'stage').map((a) => a.fieldName);
    expect(fields).toEqual(expect.arrayContaining(['Name', 'SortOrder', 'Probability']));
  });
  it('requires exactly one Won and one Lost stage, unique names, 0-100 probabilities', async () => {
    const s = await session();
    const noWon = await s.post('/settings/stages', { ...stageForm(s, { type_4: 'open' }), action: 'save' });
    expect(noWon.status).toBe(422);
    expect(noWon.text).toContain('You need exactly one Won stage and one Lost stage.');
    expect((await s.post('/settings/stages', { ...stageForm(s, { type_0: 'won' }), action: 'save' })).text).toContain('exactly one Won stage');
    expect((await s.post('/settings/stages', { ...stageForm(s, { name_1: 'qualification' }), action: 'save' })).text).toContain('Each stage needs a different name.');
    expect((await s.post('/settings/stages', { ...stageForm(s, { prob_0: '101' }), action: 'save' })).text).toContain('Probability must be between 0 and 100.');
    expect((await s.post('/settings/stages', { ...stageForm(s, { name_0: '' }), action: 'save' })).text).toContain('Enter a stage name.');
    expect(s.state.stages[0]!.name).toBe('Qualification');
  });
  it('Won is forced to 100 and Lost to 0', () => {
    const r = validateStages([{ id: '1', name: 'A', prob: '5', type: 'open' }, { id: '2', name: 'W', prob: '3', type: 'won' }, { id: '3', name: 'L', prob: '9', type: 'lost' }]);
    expect(r.rows.map((x) => x.probability)).toEqual([5, 100, 0]);
    expect(r.summary).toEqual([]);
  });
  it('removing a stage in use is refused with the count; an unused one can be removed', async () => {
    const s = await session();
    const idx = s.state.stages.findIndex((x) => x.id === 3); // Proposal has an opportunity
    const keep = s.state.stages.filter((_, i) => i !== idx);
    const body: Record<string, string> = { n: String(keep.length) };
    keep.forEach((st, i) => Object.assign(body, { [`id_${i}`]: String(st.id), [`name_${i}`]: st.name, [`prob_${i}`]: String(st.probability), [`type_${i}`]: st.type }));
    const blocked = await s.post('/settings/stages', { ...body, action: 'save' });
    expect(blocked.status).toBe(422);
    expect(blocked.text).toContain('Move 1 opportunity out of &#34;Proposal&#34; before deleting it.');
    s.state.data.opportunities = s.state.data.opportunities.filter((o) => o.StageId !== 3);
    expect((await s.post('/settings/stages', { ...body, action: 'save' })).status).toBe(303);
    expect(s.state.stages.some((x) => x.id === 3)).toBe(false);
    expect(audit(s, 'stage').find((a) => a.action === 'D')).toMatchObject({ oldValue: 'Proposal' });
  });
  it('a stage change applies immediately to opportunity forms', async () => {
    const s = await session();
    await s.post('/settings/stages', { ...stageForm(s, { name_0: 'Discovery' }), action: 'save' });
    expect((await s.agent.get('/opportunities/new')).text).toContain('Discovery (10%)');
  });
});

describe('picklists', () => {
  it('adds, renames, deactivates, reactivates and reorders values, all audited; no duplicates', async () => {
    const s = await session();
    expect((await s.post('/settings/picklists/industry', { action: 'add', label: 'Energy' })).status).toBe(303);
    const list = s.state.picklists.industry!;
    const energy = list.at(-1)!;
    expect(energy.label).toBe('Energy');
    expect((await s.post('/settings/picklists/industry', { action: 'add', label: 'energy' })).text).toContain('already exists');
    await s.post('/settings/picklists/industry', { action: 'rename', valueId: String(energy.id), label: 'Energy & Utilities' });
    expect(list.at(-1)!.label).toBe('Energy & Utilities');
    await s.post('/settings/picklists/industry', { action: 'deactivate', valueId: String(energy.id) });
    expect(list.at(-1)!.active).toBe(false);
    expect((await s.agent.get('/accounts/new')).text).not.toContain('Energy &amp; Utilities');
    await s.post('/settings/picklists/industry', { action: 'activate', valueId: String(energy.id) });
    await s.post('/settings/picklists/industry', { action: 'up', valueId: String(energy.id) });
    expect(list.at(-2)!.label).toBe('Energy & Utilities');
    expect(audit(s, 'picklistvalue').map((a) => a.action + (a.fieldName ?? ''))).toEqual(['C', 'ULabel', 'UIsActive', 'UIsActive', 'USortOrder']);
  });
  it('validates labels, and activity types cannot be added', async () => {
    const s = await session();
    expect((await s.post('/settings/picklists/industry', { action: 'add', label: '' })).text).toContain('Enter a name.');
    expect((await s.post('/settings/picklists/industry', { action: 'add', label: 'x'.repeat(81) })).status).toBe(422);
    expect((await s.post('/settings/picklists/activity_type', { action: 'add', label: 'Lunch' })).status).toBe(422);
    expect((await s.post('/settings/picklists/industry', { action: 'bogus', valueId: '1' })).status).toBe(422);
    expect((await s.post('/settings/picklists/nope', { action: 'add', label: 'x' })).status).toBe(404);
  });
});

describe('visibility', () => {
  it('asks for confirmation, then applies immediately to what lists show; audited', async () => {
    const s = await session('Admin');
    const u = request.agent(s.app);
    await u.get('/__test/login?role=User');
    const names = async () => [...(await u.get('/accounts')).text.matchAll(/<a href="\/accounts\/\d+">([^<]+)<\/a>/g)].map((m) => m[1]);
    expect(await names()).toEqual(['Acme A/S']);
    const confirm = await s.post('/settings/visibility', { mode: 'team' });
    expect(confirm.text).toContain('This changes what everyone can see immediately. Continue?');
    expect(s.state.settings.visibilityMode).toBe('private');
    expect((await s.post('/settings/visibility', { mode: 'team', confirm: '1' })).status).toBe(303);
    expect(s.state.settings.visibilityMode).toBe('team');
    expect((await names()).length).toBeGreaterThan(1);
    expect(audit(s, 'setting')).toEqual([expect.objectContaining({ fieldName: 'visibility.mode', oldValue: 'private', newValue: 'team' })]);
  });
  it('shows the Managers note, rejects bad modes, and needs no confirmation when unchanged', async () => {
    const s = await session();
    expect((await s.agent.get('/settings/visibility')).text).toContain("Managers always see their team's records.");
    expect((await s.post('/settings/visibility', { mode: 'everyone' })).status).toBe(422);
    expect((await s.post('/settings/visibility', { mode: 'private' })).status).toBe(303);
    expect(audit(s, 'setting')).toHaveLength(0);
  });
});

describe('users & teams', () => {
  const GROUP = '11111111-2222-4333-8444-555555555555';
  it('lists users read-only with role, team and warning badges, and filters', async () => {
    const s = await session();
    s.state.users.find((u) => u.id === 4)!.teamStatus = 'multiple';
    s.state.users.find((u) => u.id === 4)!.teamId = null;
    const html = (await s.agent.get('/settings/users')).text;
    expect(html).toContain('No team — sees own records only');
    expect(html).toContain('In several teams — check Entra groups');
    expect(html).toContain('Show them');
    expect(html).not.toMatch(/<form[^>]*method="post"[^>]*action="\/settings\/users/);
    expect((await s.agent.get('/settings/users?role=Admin')).text).not.toContain('Otto Otherteam');
    expect((await s.agent.get('/settings/users?team=none')).text).toContain('Nina NoTeam');
  });
  it('adds a team (name + GUID, normalised to lower case), audited', async () => {
    const s = await session();
    const res = await s.post('/settings/teams', { name: 'Finance', groupId: ` {${GROUP.toUpperCase()}} ` });
    expect(res.status).toBe(303);
    expect(s.state.teams.at(-1)).toMatchObject({ name: 'Finance', groupId: GROUP, active: true });
    expect(audit(s, 'team')).toEqual([expect.objectContaining({ action: 'C', newValue: 'Finance' })]);
    expect((await s.agent.get('/settings/users')).text).toContain('Team Finance added.');
  });
  it('validates with the exact copy and keeps input', async () => {
    const s = await session();
    const empty = await s.post('/settings/teams', { name: '', groupId: '' });
    expect(empty.status).toBe(422);
    expect(empty.text).toContain('Enter a team name.');
    expect(empty.text).toContain('Enter the Entra group object ID.');
    const bad = await s.post('/settings/teams', { name: 'Kept', groupId: 'not-a-guid' });
    expect(bad.text).toContain('Enter the group object ID in this format: 00000000-0000-0000-0000-000000000000.');
    expect(bad.text).toContain('value="Kept"');
    expect((await s.post('/settings/teams', { name: 'Sales North', groupId: GROUP })).text).toContain('A team named Sales North already exists.');
    expect((await s.post('/settings/teams', { name: 'Other', groupId: '3F2B9C1E-7A4D-4E8B-9C0F-1A2B3C4D5E6F' })).text).toContain('This group is already linked to the team Sales North.');
    expect(s.state.teams).toHaveLength(2);
    expect(validateTeam({ name: 'x'.repeat(81), groupId: GROUP }).errors.name).toContain('80');
  });
  it('edits a team; changing the group needs confirmation', async () => {
    const s = await session();
    expect((await s.post('/settings/teams/1', { name: 'North', groupId: s.state.teams[0]!.groupId })).status).toBe(303);
    expect(s.state.teams[0]!.name).toBe('North');
    const confirm = await s.post('/settings/teams/1', { name: 'North', groupId: GROUP });
    expect(confirm.text).toContain('Changing the group changes who is in North. People are moved at their next sign-in. Continue?');
    expect(s.state.teams[0]!.groupId).not.toBe(GROUP);
    expect((await s.post('/settings/teams/1', { name: 'North', groupId: GROUP, confirm: '1' })).status).toBe(303);
    expect(s.state.teams[0]!.groupId).toBe(GROUP);
    expect(audit(s, 'team').map((a) => a.fieldName)).toEqual(['Name', 'EntraGroupId']);
    expect((await s.post('/settings/teams/99', { name: 'x', groupId: GROUP })).status).toBe(404);
  });
  it('deactivates with a confirmation naming the member count, and reactivates; teams are never deleted', async () => {
    const s = await session();
    const confirm = await s.agent.get('/settings/teams/1/deactivate');
    expect(confirm.text).toContain('Deactivate Sales North? Its 4 members will have no team from their next sign-in');
    expect((await s.post('/settings/teams/1/deactivate', {})).status).toBe(303);
    expect(s.state.teams[0]!.active).toBe(false);
    expect((await s.agent.get('/settings/users')).text).toContain('Reactivate Sales North');
    await s.post('/settings/teams/1/reactivate', {});
    expect(s.state.teams[0]!.active).toBe(true);
    expect(audit(s, 'team').map((a) => `${a.fieldName}:${a.newValue}`)).toEqual(['IsActive:0', 'IsActive:1']);
  });
});

describe('security of the settings pages', () => {
  it('every settings POST needs a CSRF token', async () => {
    const s = await session();
    for (const p of ['/settings/general', '/settings/locale', '/settings/fx', '/settings/stages', '/settings/visibility', '/settings/teams', '/settings/picklists/industry']) {
      expect((await s.agent.post(p).type('form').send({ appName: 'x' })).status, p).toBe(403);
    }
  });
  it('no inline script or style on any settings page, and values are escaped', async () => {
    const s = await session();
    await s.post('/settings/teams', { name: '<img src=x onerror=1>', groupId: '11111111-2222-4333-8444-555555555555' });
    for (const p of ['/settings/general', '/settings/locale', '/settings/fx', '/settings/stages', '/settings/picklists/industry', '/settings/visibility', '/settings/users']) {
      const html = (await s.agent.get(p)).text;
      expect(html, p).not.toMatch(/<script(?![^>]*\ssrc=)/i);
      expect(html, p).not.toMatch(/\sstyle=/i);
      expect(html, p).not.toContain('<img src=x');
    }
  });
});
