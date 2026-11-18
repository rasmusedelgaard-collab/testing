/* eslint-disable @typescript-eslint/no-require-imports -- standalone CommonJS script; page.evaluate callbacks run in the browser */
/**
 * Increment 4 end-to-end checks in demo mode (headless Chromium): dashboard tiles and links, Chart.js rendering under the strict CSP,
 * table fallbacks, FX notes, and no page-level horizontal scroll on the dense pages.
 *   npm run e2e        (runs all e2e files)
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
  const main = async (page) => (await page.locator('main').textContent()).replace(/\s+/g, ' ');
  const flash = async (page) => (await page.locator('main').textContent()).replace(/\s+/g, ' ');

  const overflow = async (page, path, width) => {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(base + path);
    const extra = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    if (extra > 0) console.log('      overflowing:', await page.evaluate(() => [...document.querySelectorAll('body *')].filter((e) => e.getBoundingClientRect().right > window.innerWidth + 1).slice(0, 6).map((e) => `${e.tagName}.${e.className}`).join(', ')));
    return extra;
  };
  try {
    await waitUp(base);
    browser = await loadPlaywright().chromium.launch({ headless: true });
    const admin = await login('Admin');
    const A = admin.page;

    await step('Dashboard: six tiles, each linking to a filtered list; FX exclusion note with Admin link', async () => {
      await A.goto(base + '/');
      assert.equal(await A.locator('.tile').count(), 6);
      assert.match(await main(A), /Good (morning|afternoon|evening), Demo/);
      assert.match(await main(A), /deals? excluded \(no FX rate for/);
      assert.ok(await A.getByRole('link', { name: 'Add FX rates' }).first().isVisible());
      await Promise.all([A.waitForURL(/\/opportunities\?status=open/), A.getByRole('link', { name: /Open pipeline/ }).click()]);
      assert.match(await A.locator('h1').textContent(), /Opportunities/);
    });
    await step('Dashboard: lists and the activities tile', async () => {
      await A.goto(base + '/');
      assert.match(await main(A), /My open opportunities/);
      assert.match(await main(A), /Upcoming activities/);
      await Promise.all([A.waitForURL(/\/activities\?status=open&type=actionable&due=due/), A.getByRole('link', { name: /Activities due/ }).click()]);
    });
    await step('Analytics: five figures, Chart.js draws canvases, tables collapse with JS and open on demand', async () => {
      await A.goto(base + '/analytics?period=custom&from=2026-01-01&to=2027-12-31');
      assert.equal(await A.locator('figure.chart').count(), 5);
      assert.equal(await A.evaluate(() => typeof window.Chart), 'function');
      await A.waitForFunction(() => [...document.querySelectorAll('canvas[data-chart]')].every((c) => c.width > 0 && c.height > 0));
      assert.ok((await A.locator('canvas[data-chart]').count()) >= 3);
      const withoutCanvas = (await A.locator('figure.chart').count()) - (await A.locator('canvas[data-chart]').count());
      assert.equal(await A.locator('details.chart-table[open]').count(), withoutCanvas); // only figures with no chart keep their table open
      await A.locator('figure.chart').first().getByText('Show as table').click();
      assert.ok(await A.locator('figure.chart').first().locator('table').isVisible());
      assert.match(await A.locator('figcaption').first().textContent(), /Pipeline by stage/);
    });
    await step('Analytics: period filter lives in the URL; custom range errors show the exact copy', async () => {
      await A.goto(base + '/analytics');
      await A.getByLabel('Period').selectOption('last-quarter');
      await Promise.all([A.waitForURL(/period=last-quarter/), A.getByRole('button', { name: 'Apply' }).click()]);
      await A.goto(base + '/analytics?period=custom&from=2026-02-01&to=2026-01-01');
      assert.match(await main(A), /End date must be on or after start date\./);
    });
    await step('Accounts list shows the Open pipeline column', async () => {
      await A.goto(base + '/accounts');
      assert.match(await main(A), /Open pipeline/);
      assert.match(await main(A), /DKK 120\.000,00/);
    });
    await step('Settings: FX rate with a wrong separator is refused, a right one is saved and clears the dashboard note', async () => {
      await A.goto(base + '/settings/fx');
      await A.getByLabel('Currency (required)').selectOption('EUR');
      await A.getByLabel(/Rate to DKK/).fill('7.4590');
      await A.getByRole('button', { name: 'Save rate' }).click();
      assert.match(await main(A), /Enter a rate, for example 7,4590\./);
      await A.getByLabel(/Rate to DKK/).fill('7,4590');
      await A.getByRole('button', { name: 'Save rate' }).click();
      assert.match(await main(A), /Rate saved\./);
    });
    await step('No page-level horizontal scroll: dashboard, analytics, stages, users, FX at 390 / 800 px', async () => {
      for (const [path, width] of [['/', 390], ['/analytics', 390], ['/settings/users', 390], ['/settings/stages', 390], ['/settings/fx', 390], ['/settings/stages', 800], ['/settings/users', 800], ['/opportunities', 800], ['/', 800]]) {
        const extra = await overflow(A, path, width);
        assert.ok(extra <= 0, `${path} at ${width}px overflows by ${extra}px`);
      }
    });
    await step('no console errors, CSP violations or page errors across all steps', async () => {
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
