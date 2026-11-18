/* eslint-disable no-undef, @typescript-eslint/no-require-imports -- standalone CommonJS script; page.evaluate callbacks run in the browser */
/**
 * End-to-end smoke test of increment 1 in demo mode (no database, no Entra), driven by headless Chromium.
 *   npm run e2e            (starts its own server on a free port, exits non-zero on any failure)
 * Needs the `playwright` package and a Chromium build. Resolution order: ./node_modules, then PLAYWRIGHT_MODULE_PATH.
 */
const { spawn } = require('node:child_process');
const assert = require('node:assert/strict');
const net = require('node:net');
const path = require('node:path');

function loadPlaywright() {
  const candidates = ['playwright', process.env.PLAYWRIGHT_MODULE_PATH, '/opt/node-tools/node_modules/playwright'].filter(Boolean);
  for (const c of candidates) {
    try { return require(c); } catch { /* try next */ }
  }
  throw new Error('playwright not found: npm i -D playwright, or set PLAYWRIGHT_MODULE_PATH');
}

const freePort = () => new Promise((resolve, reject) => {
  const s = net.createServer();
  s.listen(0, () => { const { port } = s.address(); s.close(() => resolve(port)); });
  s.on('error', reject);
});

async function waitUp(url) {
  for (let i = 0; i < 60; i++) {
    try { if ((await fetch(url + '/healthz')).ok) return; } catch { /* not yet */ }
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error('server did not start');
}

const results = [];
async function step(name, fn) {
  try { await fn(); results.push([name, true]); console.log('ok   ', name); }
  catch (e) { results.push([name, false, e]); console.log('FAIL ', name, '\n      ', e.message.split('\n').join('\n       ')); }
}

(async () => {
  const root = path.resolve(__dirname, '..');
  const port = await freePort();
  const base = `http://127.0.0.1:${port}`;
  const server = spawn(process.execPath, [require.resolve('tsx/cli', { paths: [root] }), 'src/server.ts'], {
    cwd: root,
    env: { ...process.env, NODE_ENV: 'development', DEMO_MODE: 'true', PORT: String(port), LOG_LEVEL: 'silent' },
    stdio: 'inherit',
  });
  let browser;
  try {
    await waitUp(base);
    const { chromium } = loadPlaywright();
    browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const page = await ctx.newPage();
    const consoleProblems = [];
    // The deliberate 404 navigation logs a generic 'Failed to load resource'; everything else (incl. CSP 'Refused to ...') counts.
    const expectedNavErrors = ['/no-such-page'];
    page.on('console', (m) => {
      if (!['error', 'warning'].includes(m.type())) return;
      if (expectedNavErrors.some((u) => m.location().url.endsWith(u))) return;
      consoleProblems.push(`${m.type()}: ${m.text()} (${m.location().url})`);
    });
    page.on('pageerror', (e) => consoleProblems.push(`pageerror: ${e.message}`));
    const failedRequests = [];
    page.on('response', (r) => { if (r.status() >= 400 && !r.url().endsWith('/favicon.ico') && !r.request().isNavigationRequest()) failedRequests.push(`${r.status()} ${r.url()}`); });

    await step('unauthenticated / redirects to /signin with returnTo', async () => {
      await page.goto(base + '/accounts');
      assert.equal(new URL(page.url()).pathname, '/signin');
      assert.equal(new URL(page.url()).searchParams.get('returnTo'), '/accounts');
      assert.match(await page.textContent('h1'), /Sign in/);
    });

    await step('sign-in page: demo banner, skip link first in tab order, lang, title', async () => {
      assert.match(await page.textContent('.env-banner'), /DEMO MODE/);
      assert.equal(await page.getAttribute('html', 'lang'), 'en');
      assert.match(await page.title(), /Sign in/);
      await page.keyboard.press('Tab');
      const focused = await page.evaluate(() => ({ cls: document.activeElement.className, href: document.activeElement.getAttribute('href') }));
      assert.equal(focused.cls, 'skip-link');
      assert.equal(focused.href, '#main');
      const box = await page.locator('.skip-link').boundingBox();
      assert.ok(box && box.y >= 0, 'skip link visible when focused');
    });

    await step('skip link moves focus to <main id=main>', async () => {
      await page.keyboard.press('Enter');
      assert.equal(await page.evaluate(() => document.activeElement.id), 'main');
    });

    await step('demo sign-in as Admin lands on returnTo (/accounts)', async () => {
      await page.getByRole('radio', { name: 'Admin' }).check();
      await Promise.all([page.waitForURL('**/accounts'), page.getByRole('button', { name: /Sign in as demo user/ }).click()]);
      assert.match(await page.textContent('h1'), /Accounts/);
    });

    await step('layout: nav landmarks, current page marked, Settings visible for Admin', async () => {
      const nav = page.getByRole('navigation', { name: 'Main' });
      for (const label of ['Dashboard', 'Accounts', 'Contacts', 'Opportunities', 'Activities', 'Analytics', 'Settings']) {
        assert.equal(await nav.getByRole('link', { name: label, exact: true }).count(), 1, label);
      }
      assert.equal(await nav.locator('[aria-current="page"]').textContent(), 'Accounts');
      assert.equal(await page.locator('main#main').count(), 1);
      assert.equal(await page.locator('header.topbar').count(), 1);
    });

    await step('every nav link resolves to a page (no dead links), each with one h1', async () => {
      const hrefs = await page.locator('#main-nav a').evaluateAll((as) => as.map((a) => a.getAttribute('href')));
      for (const h of hrefs) {
        const res = await page.goto(base + h);
        assert.equal(res.status(), 200, h);
        assert.equal(await page.locator('h1').count(), 1, h + ' h1');
      }
    });

    await step('user menu shows role badge and sign-out form with CSRF field', async () => {
      await page.goto(base + '/');
      await page.locator('details.user-menu > summary').click();
      assert.match(await page.locator('.badge').first().textContent(), /Role: Admin/);
      assert.equal(await page.locator('form[action="/auth/signout"] input[name=_csrf]').count(), 1);
    });

    await step('404 page for unknown URL when signed in (status 404, reference shown, layout intact)', async () => {
      const res = await page.goto(base + '/no-such-page');
      assert.equal(res.status(), 404);
      assert.match(await page.textContent('h1'), /can't find/i);
      assert.match(await page.textContent('main'), /Reference: [0-9a-f-]{36}/);
      assert.equal(await page.locator('#main-nav').count(), 1);
    });

    await step('responses carry a strict CSP and no inline script/style is present', async () => {
      const res = await page.goto(base + '/');
      const csp = res.headers()['content-security-policy'];
      assert.match(csp, /script-src 'self'/);
      assert.doesNotMatch(csp, /unsafe-inline|unsafe-eval/);
      assert.equal(await page.locator('script:not([src])').count(), 0);
      assert.equal(await page.locator('[style]').count(), 0);
      assert.equal(await page.locator('style').count(), 0);
    });

    await step('Manager and User: no Settings link, /settings shows 403 page', async () => {
      for (const role of ['Manager', 'User']) {
        const p2 = await ctx.newPage();
        await p2.goto(base + '/signin');
        // fresh context needed for a different identity: sign out first
        await p2.close();
        const c2 = await browser.newContext();
        const pg = await c2.newPage();
        await pg.goto(base + '/signin');
        await pg.getByRole('radio', { name: role }).check();
        await pg.getByRole('button', { name: /Sign in as demo user/ }).click();
        await pg.waitForURL(base + '/');
        assert.equal(await pg.locator('#main-nav a[href^="/settings"]').count(), 0, role + ' nav');
        const res = await pg.goto(base + '/settings/general');
        assert.equal(res.status(), 403, role);
        assert.match(await pg.textContent('h1'), /don't have access/i);
        assert.match(await pg.textContent('main'), /Reference: /);
        await c2.close();
      }
    });

    await step('mobile viewport: nav hidden behind Menu button, toggles with aria-expanded and Escape', async () => {
      const c3 = await browser.newContext({ viewport: { width: 390, height: 800 } });
      const m = await c3.newPage();
      await m.goto(base + '/signin');
      await m.getByRole('button', { name: /Sign in as demo user/ }).click();
      await m.waitForURL(base + '/');
      const btn = m.locator('#menu-button');
      assert.equal(await btn.isVisible(), true);
      assert.equal(await btn.getAttribute('aria-expanded'), 'false');
      await btn.click();
      assert.equal(await btn.getAttribute('aria-expanded'), 'true');
      assert.equal(await m.locator('#main-nav').isVisible(), true);
      await m.keyboard.press('Escape');
      assert.equal(await btn.getAttribute('aria-expanded'), 'false');
      const overflow = await m.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
      assert.equal(overflow, false, 'no horizontal scroll at 390px');
      await c3.close();
    });

    await step('sign out ends the session: protected page redirects again', async () => {
      await page.goto(base + '/');
      await page.locator('details.user-menu > summary').click();
      await Promise.all([page.waitForURL('**/signed-out'), page.getByRole('button', { name: 'Sign out' }).click()]);
      await page.goto(base + '/accounts');
      assert.equal(new URL(page.url()).pathname, '/signin');
    });

    await step('/__test/login is not available outside NODE_ENV=test', async () => {
      await page.goto(base + '/__test/login?role=Admin');
      assert.equal(new URL(page.url()).pathname, '/signin');
    });

    await step('no CSP violations, console errors or failed sub-resource requests during the whole run', async () => {
      assert.deepEqual(consoleProblems, []);
      assert.deepEqual(failedRequests, []);
    });
  } finally {
    if (browser) await browser.close();
    server.kill('SIGTERM');
  }
  const failed = results.filter((r) => !r[1]);
  console.log(`\n${results.length - failed.length}/${results.length} e2e steps passed`);
  process.exit(failed.length ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
