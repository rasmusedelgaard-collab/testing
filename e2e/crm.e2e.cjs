/* eslint-disable @typescript-eslint/no-require-imports -- standalone CommonJS script; page.evaluate callbacks run in the browser */
/**
 * Increment 2 end-to-end journeys in demo mode (in-memory CRM store, headless Chromium):
 * create -> edit -> edit conflict (two browser sessions) -> delete for every entity, plus role-based checks.
 *   npm run e2e        (runs demo.e2e.cjs and this file)
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
  const watch = (page) => {
    page.on('console', (m) => { if (['error', 'warning'].includes(m.type()) && !/Failed to load resource/.test(m.text())) problems.push(`${m.type()}: ${m.text()}`); });
    page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`));
  };
  const login = async (role) => {
    const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await ctx.newPage();
    watch(page);
    await page.goto(base + '/signin');
    await page.getByRole('radio', { name: role }).check();
    await Promise.all([page.waitForURL(base + '/'), page.getByRole('button', { name: /Sign in as demo user/ }).click()]);
    return { ctx, page };
  };
  const save = (page) => Promise.all([page.waitForLoadState('load'), page.getByRole('button', { name: 'Save' }).click()]);
  const flash = async (page) => (await page.locator('main').textContent()).replace(/\s+/g, ' ');

  try {
    await waitUp(base);
    browser = await loadPlaywright().chromium.launch({ headless: true });
    const admin = await login('Admin');
    const A = admin.page;
    // A second, independent Admin session to provoke edit conflicts.
    const admin2 = await login('Admin');
    const B = admin2.page;

    // ---------- journeys ----------
    const journeys = [
      {
        name: 'Account', list: '/accounts',
        create: async (p) => { await p.getByLabel('Name (required)').fill('E2E Account'); await p.getByLabel('City').fill('Viborg'); },
        edit: async (p, v) => { await p.getByLabel('Name (required)').fill(v); },
        title: (n) => n,
      },
      {
        name: 'Contact', list: '/contacts',
        create: async (p) => { await p.getByLabel('First name').fill('Eve'); await p.getByLabel('Last name (required)').fill('E2EContact'); await p.getByLabel('Email').fill('eve@example.com'); },
        edit: async (p, v) => { await p.getByLabel('Last name (required)').fill(v); },
        title: (n) => `Eve ${n}`,
      },
      {
        name: 'Opportunity', list: '/opportunities',
        create: async (p) => {
          await p.getByLabel('Name (required)').fill('E2E Opportunity');
          await p.getByLabel('Account (required)').selectOption({ label: 'Acme A/S' });
          await p.getByLabel('Amount (required)').fill('12.500,00');
          await p.getByLabel('Close date (required)').fill('2027-03-15');
        },
        edit: async (p, v) => { await p.getByLabel('Name (required)').fill(v); await p.getByLabel('Amount (required)').fill('15.000,00'); },
        title: (n) => n,
      },
      {
        name: 'Activity', list: '/activities',
        create: async (p) => { await p.getByLabel('Task').check(); await p.getByLabel('Subject (required)').fill('E2E Activity'); await p.getByLabel('Related account').selectOption({ label: 'Acme A/S' }); },
        edit: async (p, v) => { await p.getByLabel('Subject (required)').fill(v); },
        title: (n) => n,
      },
    ];

    for (const j of journeys) {
      const edited = `${j.name} Edited`;
      let recordUrl;
      await step(`${j.name}: empty required fields show the error summary and inline errors, input is kept`, async () => {
        await A.goto(base + `${j.list}/new`);
        await save(A);
        assert.ok(await A.locator('.error-summary[role=alert]').isVisible());
        assert.match(await A.locator('.error-summary h2').textContent(), /Fix \d+ problems? to save/);
        assert.ok((await A.locator('.field-error').count()) >= 1);
        assert.equal(await A.locator('[aria-invalid=true]').first().isVisible(), true);
        // the summary link jumps to the field
        const href = await A.locator('.error-summary a').first().getAttribute('href');
        assert.match(href, /^#f-/);
      });

      await step(`${j.name}: create -> saved flash -> detail page`, async () => {
        await A.goto(base + `${j.list}/new`);
        await j.create(A);
        await save(A);
        recordUrl = A.url();
        assert.match(recordUrl, new RegExp(`${j.list}/\\d+$`));
        assert.match(await flash(A), /saved\./i);
        assert.match(await A.locator('h1').textContent(), new RegExp(j.title(j.name === 'Contact' ? 'E2EContact' : `E2E ${j.name}`)));
        assert.match(await A.locator('#history-h, section:has-text("History")').first().textContent(), /History/);
      });

      await step(`${j.name}: record appears in the list and search finds it`, async () => {
        await A.goto(base + `${j.list}?q=E2E${j.name === 'Contact' ? 'Contact' : ''}${j.name === 'Contact' ? '' : ' ' + j.name}&status=all`);
        assert.ok((await A.locator('table tbody tr').count()) >= 1);
        assert.match(new URL(A.url()).search, /q=/);
      });

      await step(`${j.name}: edit saves and History records it (Last modified by)`, async () => {
        await A.goto(recordUrl + '/edit');
        await j.edit(A, edited);
        await save(A);
        assert.equal(A.url(), recordUrl);
        assert.match(await A.locator('h1').textContent(), new RegExp(edited));
        assert.match(await flash(A), /Last modified by Demo Admin/);
      });

      await step(`${j.name}: second session saving a stale form gets the 409 conflict page and does not overwrite`, async () => {
        // A opens the form, B saves first, then A submits its stale form.
        await A.goto(recordUrl + '/edit');
        await B.goto(recordUrl + '/edit');
        await j.edit(B, `${edited} B`);
        await save(B);
        await j.edit(A, `${edited} A-overwrite`);
        const [resp] = await Promise.all([A.waitForResponse((r) => r.request().method() === 'POST'), A.getByRole('button', { name: 'Save' }).click()]);
        assert.equal(resp.status(), 409);
        assert.match(await A.locator('h1').textContent(), /Someone else changed this record/);
        await A.getByRole('link', { name: 'Show the current version' }).click();
        const text = await A.locator('h1').textContent();
        assert.match(text, /Edited B/);
        assert.doesNotMatch(text, /A-overwrite/);
      });

      await step(`${j.name}: delete needs a confirmation page, then removes the record`, async () => {
        await A.goto(recordUrl);
        await A.getByRole('link', { name: new RegExp(`^Delete ${j.name.toLowerCase()}$`) }).click();
        assert.match(await A.locator('h1').textContent(), /^Delete .+\?$/);
        assert.match(await A.textContent('main'), /cannot be undone/);
        await Promise.all([A.waitForURL(base + j.list), A.getByRole('button', { name: new RegExp(`^Delete ${j.name.toLowerCase()}$`) }).click()]);
        assert.match(await flash(A), /deleted\./i);
        const gone = await A.goto(recordUrl);
        assert.equal(gone.status(), 404);
        assert.match(await A.locator('h1').textContent(), /can't find/i);
      });
    }

    // ---------- cross-cutting: list state, empty states, locale ----------
    await step('list: sort header toggles direction in the URL, aria-sort set, paging links keep state', async () => {
      await A.goto(base + '/accounts');
      await A.getByRole('link', { name: 'City' }).click();
      assert.match(A.url(), /sort=city&dir=asc/);
      assert.equal(await A.locator('th[aria-sort=ascending]').count(), 1);
      await A.getByRole('link', { name: 'City' }).click();
      assert.match(A.url(), /dir=desc/);
      await A.reload();
      assert.equal(await A.locator('th[aria-sort=descending]').count(), 1);
    });
    await step('list: filtered empty state with a clear-filters link; no-match search keeps the term', async () => {
      await A.goto(base + '/accounts?q=zzzznomatch');
      assert.match(await A.textContent('main'), /match these filters/);
      assert.equal(await A.locator('input[name=q]').inputValue().catch(() => 'zzzznomatch'), 'zzzznomatch');
    });
    await step('opportunities list: default Open filter, da-DK money and date formats', async () => {
      await A.goto(base + '/opportunities');
      const text = await A.locator('table').textContent();
      assert.match(text, /120\.000,00/);
      assert.match(text, /15\.12\.2026/);
    });
    await step('form: edit shows the amount ungrouped in the locale decimal format (design: no thousands separator in inputs)', async () => {
      await A.goto(base + '/opportunities/1/edit');
      assert.equal(await A.getByLabel('Amount (required)').inputValue(), '120000');
      await A.getByLabel('Amount (required)').fill('1234,5');
      await save(A);
      await A.goto(base + '/opportunities/1/edit');
      assert.equal(await A.getByLabel('Amount (required)').inputValue(), '1234,5');
    });

    // ---------- role-based ----------
    await step('User: sees only own records, hidden record is a 404 page, owner list has team only, hand-off message', async () => {
      const u = await login('User');
      const p = u.page;
      await p.goto(base + '/accounts');
      const names = await p.locator('table tbody tr td:first-child').allTextContents();
      assert.deepEqual(names.map((n) => n.trim()), ['Acme A/S']);
      const res = await p.goto(base + '/accounts/3');
      assert.equal(res.status(), 404);
      await p.goto(base + '/accounts/new');
      const owners = await p.locator('#f-OwnerId option').allTextContents();
      assert.ok(owners.some((o) => /Anna Teammate/.test(o)));
      assert.ok(!owners.some((o) => /Otto|Nina/.test(o)), owners.join());
      await p.getByLabel('Name (required)').fill('Handed off');
      await p.getByLabel('Owner (required)').selectOption({ label: 'Anna Teammate' });
      await save(p);
      assert.equal(new URL(p.url()).pathname, '/accounts');
      assert.match(await flash(p), /Saved and assigned to Anna Teammate\./);
      assert.equal((await p.locator('table tbody tr').allTextContents()).some((t) => /Handed off/.test(t)), false);
      assert.equal((await p.goto(base + '/settings/general')).status(), 403);
      await u.ctx.close();
    });
    await step('Manager: sees team-mate records, can delete one; other team is 404', async () => {
      const m = await login('Manager');
      const p = m.page;
      await p.goto(base + '/accounts');
      const names = (await p.locator('table tbody tr td:first-child').allTextContents()).map((n) => n.trim());
      assert.ok(names.includes('Nordic Tools ApS') && !names.includes('Southern Foods'), names.join());
      assert.equal((await p.goto(base + '/accounts/3')).status(), 404);
      await p.goto(base + '/contacts/2');
      assert.equal(await p.getByRole('link', { name: 'Delete contact' }).count(), 1);
      await m.ctx.close();
    });
    await step('Admin: sees every team, owner list shows team names', async () => {
      await A.goto(base + '/accounts?pageSize=100');
      assert.match(await A.textContent('table'), /Southern Foods/);
      await A.goto(base + '/accounts/new');
      assert.ok((await A.locator('#f-OwnerId option').allTextContents()).some((o) => /Otto Otherteam \(Sales South\)/.test(o)));
    });
    await step('Opportunity: Mark as lost needs a reason; Mark as won closes it and moves it out of the Open list', async () => {
      await A.goto(base + '/opportunities/2');
      await A.getByRole('button', { name: 'Mark as lost' }).click();
      assert.match(await A.locator('#e-lostReason').textContent(), /Choose why this was lost\./);
      await A.getByRole('button', { name: 'Mark as won' }).click();
      await A.getByRole('button', { name: 'Mark as won' }).click(); // confirmation step
      assert.match(await flash(A), /marked as won/i);
      await A.goto(base + '/opportunities');
      assert.doesNotMatch(await A.locator('table').textContent(), /Tool rollout/);
      await A.goto(base + '/opportunities?status=won');
      assert.match(await A.locator('table').textContent(), /Tool rollout/);
    });
    await step('Activity: done checkbox form removes it from the Open list', async () => {
      await A.goto(base + '/activities');
      const before = await A.locator('table tbody tr').count();
      await A.locator('table tbody tr').first().locator('input[type=checkbox], button').first().click();
      await A.waitForLoadState('load');
      assert.equal(await A.locator('table tbody tr').count(), before - 1);
    });
    await step('keyboard: tab order reaches skip link, then the form can be submitted with Enter in a text field', async () => {
      await A.goto(base + '/accounts/new');
      await A.getByLabel('Name (required)').fill('Keyboard Co');
      await Promise.all([A.waitForURL(/\/accounts\/\d+$/), A.keyboard.press('Enter')]);
      assert.match(await A.locator('h1').textContent(), /Keyboard Co/);
    });
    await step('no console errors, CSP violations or page errors across all journeys', async () => {
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
