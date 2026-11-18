/* eslint-disable @typescript-eslint/no-require-imports -- standalone CommonJS script */
/**
 * Increment 3 end-to-end: Admin settings journey in demo mode (in-memory store, headless Chromium), plus access control
 * and "takes effect immediately" checks across a second (User) session.
 *   npm run e2e        (runs demo, crm and settings scripts)
 */
const { spawn } = require('node:child_process');
const assert = require('node:assert/strict');
const net = require('node:net');
const path = require('node:path');

function loadPlaywright() {
  for (const c of ['playwright', process.env.PLAYWRIGHT_MODULE_PATH, '/opt/node-tools/node_modules/playwright'].filter(Boolean)) {
    try { return require(c); } catch { /* try next */ }
  }
  throw new Error('playwright not found: npm i -D playwright, or set PLAYWRIGHT_MODULE_PATH');
}
const freePort = () => new Promise((resolve, reject) => { const s = net.createServer(); s.listen(0, () => { const { port } = s.address(); s.close(() => resolve(port)); }); s.on('error', reject); });
async function waitUp(url) {
  for (let i = 0; i < 80; i++) { try { if ((await fetch(url + '/healthz')).ok) return; } catch { /* not yet */ } await new Promise((r) => setTimeout(r, 250)); }
  throw new Error('server did not start');
}
const results = [];
async function step(name, fn) {
  try { await fn(); results.push([name, true]); console.log('ok   ', name); }
  catch (e) { results.push([name, false]); console.log('FAIL ', name, '\n      ', String(e.message).split('\n').join('\n       ')); }
}

(async () => {
  const root = path.resolve(__dirname, '..');
  const port = await freePort();
  const base = `http://127.0.0.1:${port}`;
  const server = spawn(process.execPath, [require.resolve('tsx/cli', { paths: [root] }), 'src/server.ts'], {
    cwd: root, env: { ...process.env, NODE_ENV: 'development', DEMO_MODE: 'true', PORT: String(port), LOG_LEVEL: 'silent' }, stdio: 'inherit',
  });
  let browser;
  const problems = [];
  const login = async (role) => {
    const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await ctx.newPage();
    page.on('console', (m) => { if (['error', 'warning'].includes(m.type()) && !/Failed to load resource/.test(m.text())) problems.push(`${m.type()}: ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`));
    await page.goto(base + '/signin');
    await page.getByRole('radio', { name: role }).check();
    await Promise.all([page.waitForURL(base + '/'), page.getByRole('button', { name: /Sign in as demo user/ }).click()]);
    return { ctx, page };
  };
  const submit = (page, name) => Promise.all([page.waitForLoadState('load'), page.getByRole('button', { name }).click()]);
  const main = async (page) => (await page.locator('main').textContent()).replace(/\s+/g, ' ');

  try {
    await waitUp(base);
    browser = await loadPlaywright().chromium.launch({ headless: true });
    const admin = await login('Admin');
    const A = admin.page;
    const user = await login('User');
    const U = user.page;

    await step('Settings link opens General; sub-nav lists all seven sections with the current one marked', async () => {
      await A.getByRole('link', { name: 'Settings', exact: true }).click();
      await A.waitForURL('**/settings/general');
      const nav = A.getByRole('navigation', { name: 'Settings' });
      for (const l of ['General', 'Currency & locale', 'FX rates', 'Pipeline stages', 'Picklists', 'Visibility', 'Users & teams']) assert.equal(await nav.getByRole('link', { name: l, exact: true }).count(), 1, l);
      assert.equal(await nav.locator('[aria-current=page]').textContent(), 'General');
    });

    await step('General: bad colour and empty name show summary + inline errors; valid save renames the CRM everywhere at once', async () => {
      await A.getByLabel('CRM name (required)').fill('');
      await A.getByLabel('Accent colour').fill('#FFEB3B');
      await submit(A, 'Save');
      assert.match(await A.locator('.error-summary h2').textContent(), /Fix 2 problems to save/);
      assert.match(await main(A), /This colour is too light for text and buttons/);
      await A.getByLabel('CRM name (required)').fill('E2E CRM');
      await A.getByLabel('Accent colour').fill('#0B5394');
      await submit(A, 'Save');
      assert.match(await main(A), /Settings saved\./);
      assert.match(await A.locator('.brand').textContent(), /E2E CRM/);
      await U.goto(base + '/');
      assert.match(await U.locator('.brand').textContent(), /E2E CRM/);
      assert.match(await (await A.request.get(base + '/css/theme.css')).text(), /#0B5394/i);
    });

    await step('General: logo upload accepts a PNG and refuses any SVG with a clear message', async () => {
      const png = Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13]), Buffer.from('IHDR'), Buffer.alloc(17, 1)]); // magic + first IHDR chunk
      await A.getByLabel(/Logo file/).setInputFiles({ name: 'logo.png', mimeType: 'image/png', buffer: png });
      await submit(A, 'Upload logo');
      assert.match(await main(A), /Logo saved\./);
      await A.getByLabel(/Logo file/).setInputFiles({ name: 'bad.svg', mimeType: 'image/svg+xml', buffer: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>') });
      await submit(A, 'Upload logo');
      assert.match(await main(A), /SVG logos are not supported/);
    });

    await step('Currency & locale: switching to en-US changes list formatting immediately; switching back restores it', async () => {
      await A.goto(base + '/settings/locale');
      await A.getByLabel('Locale').selectOption('en-US');
      await submit(A, 'Save');
      assert.match(await main(A), /Settings saved\./);
      await U.goto(base + '/opportunities');
      assert.match(await U.locator('table').textContent(), /120,000\.00/);
      await A.getByLabel('Locale').selectOption('da-DK');
      await submit(A, 'Save');
      await U.goto(base + '/opportunities');
      assert.match(await U.locator('table').textContent(), /120\.000,00/);
    });

    await step('FX rates: add (Danish decimal), duplicate refused with a link to the existing rate, delete needs confirmation', async () => {
      await A.goto(base + '/settings/fx');
      await A.getByLabel('Currency (required)').selectOption('EUR');
      await A.getByLabel('Month (required)').fill('2026-11');
      await A.getByLabel(/Rate to DKK/).fill('7,4590');
      await submit(A, 'Save rate');
      assert.match(await main(A), /Rate saved\./);
      assert.match(await A.locator('table').textContent(), /7,459/);
      await A.getByLabel('Currency (required)').selectOption('EUR');
      await A.getByLabel('Month (required)').fill('2026-11');
      await A.getByLabel(/Rate to DKK/).fill('7,5');
      await submit(A, 'Save rate');
      assert.match(await main(A), /A rate for EUR in 11\.2026 already exists\./);
      assert.equal(await A.getByRole('link', { name: 'Edit the existing rate' }).count(), 1);
      await A.getByLabel(/Rate to DKK/).fill('abc');
      await A.goto(base + '/settings/fx');
      await A.getByRole('link', { name: /^Delete EUR/ }).click();
      assert.match(await main(A), /Delete the EUR rate/);
      await submit(A, 'Delete rate');
      assert.match(await main(A), /Rate deleted\./);
    });

    await step('Pipeline stages: removing an in-use stage is blocked with a count; rename and new stage show up in the opportunity form', async () => {
      await A.goto(base + '/settings/stages');
      const proposalRow = A.locator('tr', { has: A.locator('input[value="Proposal"]') });
      await proposalRow.getByRole('button', { name: /^Remove stage/ }).click();
      await A.getByRole('button', { name: 'Save stages' }).click();
      await A.waitForLoadState('load');
      assert.match(await main(A), /Move \d+ opportunit(y|ies) out of "Proposal"/);
      await A.goto(base + '/settings/stages');
      await A.getByLabel('Stage name 1').fill('Discovery');
      await A.getByRole('button', { name: 'Add stage' }).click();
      await A.waitForLoadState('load');
      await A.getByLabel('Stage name 7').fill('Contracting');
      await A.getByLabel('Probability 7 (percent)').fill('90');
      await submit(A, 'Save stages');
      assert.match(await main(A), /Settings saved\./);
      await U.goto(base + '/opportunities/new');
      const opts = await U.locator('#f-StageId option').allTextContents();
      assert.ok(opts.includes('Discovery (10%)') && opts.includes('Contracting (90%)'), opts.join());
    });

    await step('Pipeline stages: two Won stages are refused with the rule shown', async () => {
      await A.goto(base + '/settings/stages');
      await A.locator('tbody tr').first().locator('input[type=radio][value=won]').check();
      await submit(A, 'Save stages');
      assert.match(await main(A), /exactly one Won stage and one Lost stage/);
    });

    await step('Picklists: add and deactivate an industry; it leaves the new-account form but stays on existing records', async () => {
      await A.goto(base + '/settings/picklists/industry');
      await A.getByLabel('New value (required)').fill('Energy');
      await submit(A, 'Add value');
      assert.match(await main(A), /Settings saved\./);
      await A.getByRole('button', { name: 'Deactivate Energy' }).click();
      await A.waitForLoadState('load');
      assert.match(await A.locator('tr', { hasText: 'Energy' }).textContent(), /Inactive/);
      await A.goto(base + '/accounts/new');
      assert.ok(!(await A.locator('#f-IndustryValueId option').allTextContents()).includes('Energy'));
      await A.goto(base + '/settings/picklists/industry');
      await A.getByRole('button', { name: 'Deactivate Manufacturing' }).click();
      await A.waitForLoadState('load');
      await A.goto(base + '/accounts/1');
      assert.match(await main(A), /Manufacturing/);
    });

    await step('Visibility: change asks for confirmation and applies at once to another user session', async () => {
      await U.goto(base + '/accounts');
      const before = await U.locator('table tbody tr').count();
      await A.goto(base + '/settings/visibility');
      await A.getByRole('radio', { name: /^Team:/ }).check();
      await submit(A, 'Save');
      assert.match(await main(A), /This changes what everyone can see immediately\. Continue\?/);
      await submit(A, 'Change visibility');
      assert.match(await main(A), /Settings saved\./);
      await U.goto(base + '/accounts');
      assert.ok((await U.locator('table tbody tr').count()) > before);
      await A.getByRole('radio', { name: /^Private:/ }).check();
      await submit(A, 'Save');
      await submit(A, 'Change visibility');
      await U.goto(base + '/accounts');
      assert.equal(await U.locator('table tbody tr').count(), before);
    });

    await step('Users & teams: bad GUID refused with copy and input kept; valid team (braces, upper case) is added in lower case; group change asks to confirm', async () => {
      await A.goto(base + '/settings/users');
      await A.getByLabel('Team name (required)').fill('E2E Team');
      await A.getByLabel('Entra group object ID (required)').fill('not-a-guid');
      await submit(A, 'Add team');
      assert.match(await main(A), /Enter the group object ID in this format: 00000000-0000-0000-0000-000000000000\./);
      assert.equal(await A.getByLabel('Team name (required)').inputValue(), 'E2E Team');
      await A.getByLabel('Entra group object ID (required)').fill('{ABCDEF01-2345-4678-8ABC-DEF012345678}');
      await submit(A, 'Add team');
      assert.match(await main(A), /Team E2E Team added\./);
      assert.match(await A.locator('table').first().textContent(), /abcdef01-2345-4678-8abc-def012345678/);
      await A.getByRole('link', { name: 'Edit E2E Team' }).click();
      await A.getByLabel(/group object ID/i).fill('11111111-2222-4333-8444-555555555555');
      await submit(A, 'Save');
      assert.match(await main(A), /Changing the group changes who is in E2E Team/);
      await submit(A, 'Save team');
      assert.match(await main(A), /Team E2E Team saved\./);
      await A.getByRole('link', { name: 'Deactivate E2E Team' }).click();
      assert.match(await main(A), /Deactivate E2E Team\?/);
      await submit(A, 'Deactivate team');
      assert.match(await main(A), /deactivated\./);
    });

    await step('Non-admin: User and Manager sessions get the 403 page on every settings URL; no Settings link', async () => {
      const m = await login('Manager');
      for (const page of [U, m.page]) {
        assert.equal(await page.locator('#main-nav a[href^="/settings"]').count(), 0);
        for (const p of ['general', 'locale', 'fx', 'stages', 'picklists/industry', 'visibility', 'users', 'teams/1/edit']) {
          const res = await page.goto(base + '/settings/' + p);
          assert.equal(res.status(), 403, p);
          assert.match(await page.locator('h1').textContent(), /don't have access/i);
        }
      }
      await m.ctx.close();
    });

    await step('no console errors, CSP violations or page errors across the settings journey', async () => {
      assert.deepEqual(problems, []);
    });
  } finally {
    if (browser) await browser.close();
    server.kill('SIGTERM');
  }
  const failed = results.filter((r) => !r[1]);
  console.log(`\n${results.length - failed.length}/${results.length} e2e steps passed`);
  process.exit(failed.length ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
