// Renders the HTML dumped by VisualSnapshotTests (CRM_RENDER_DUMP) to PNG at deviceScaleFactor 2.
// Usage: node render.mjs <dump-dir> <out-dir> [prefix]   (needs the playwright package and its Chromium; see render.sh)
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const [dump, out, prefix = 'iter1', only = 'dashboard'] = process.argv.slice(2);
const wwwroot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../../dotnet/src/Crm.Web/wwwroot');
const types = { '.css': 'text/css', '.js': 'text/javascript', '.svg': 'image/svg+xml', '.png': 'image/png', '.html': 'text/html' };

const pages = fs.readdirSync(dump).filter(f => f.endsWith('.html'));
const find = key => pages.find(f => f.includes(key));
const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname.startsWith('/__page/')) {
    res.setHeader('content-type', 'text/html');
    return res.end(fs.readFileSync(path.join(dump, path.basename(url.pathname))));
  }
  if (url.pathname === '/css/theme.css') { res.setHeader('content-type', 'text/css'); return res.end(''); }
  const file = path.join(wwwroot, path.normalize(url.pathname).replace(/^(\.\.[/\\])+/, ''));
  if (file.startsWith(wwwroot) && fs.existsSync(file) && fs.statSync(file).isFile()) {
    res.setHeader('content-type', types[path.extname(file)] ?? 'application/octet-stream');
    return res.end(fs.readFileSync(file));
  }
  res.statusCode = 404; res.end('not found');
});
await new Promise(r => server.listen(0, r));
const base = `http://127.0.0.1:${server.address().port}`;

const allShots = [
  { name: 'dashboard-light-1440', key: 'v2-dashboard', w: 1440, h: 900, scheme: 'light' },
  { name: 'dashboard-dark-1440', key: 'v2-dashboard', w: 1440, h: 900, scheme: 'dark' },
  { name: 'pipeline-light-1440', key: 'v2-board', w: 1440, h: 900, scheme: 'light' },
  { name: 'opportunity-light-1440', key: 'v2-opportunity', w: 1440, h: 900, scheme: 'light', full: true },
  { name: 'accounts-light-1440', key: 'v2-accounts', w: 1440, h: 900, scheme: 'light' },
  { name: 'signin-light-1440', key: 'v2-signin', w: 1440, h: 900, scheme: 'light' },
  { name: 'dashboard-full-light-1440', key: 'v2-dashboard', w: 1440, h: 1800, scheme: 'light' },
  { name: 'dashboard-phone-390', key: 'v2-dashboard', w: 390, h: 844, scheme: 'light', mobile: true },
  { name: 'dashboard-phone-full-390', key: 'v2-dashboard', w: 390, h: 844, scheme: 'light', mobile: true, full: true },
  // Rollout (round 4): the other pages. Names are final file names (rollout-*.png); run with prefix "rollout" and filter "rollout".
  ...[
    ['board', 'v2-board', 'light', 1440, 900], ['opportunity', 'v2-opportunity', 'light', 1440, 900, 'full'], ['account', 'v2-account', 'light', 1440, 900, 'full'], ['opportunities', 'v2-opps', 'light', 1440, 900],
    ['activities', 'v2-acts', 'light', 1440, 900], ['opportunity-edit', 'v2-form', 'light', 1440, 900, 'full'], ['analytics', 'v2-analytics', 'light', 1440, 900, 'full'], ['settings-general', 'v2-general', 'light', 1440, 900],
    ['settings-fx', 'v2-fx', 'light', 1440, 900, 'full'], ['signin', 'v2-signin', 'light', 1440, 900], ['test-user-picker', 'v2-picker', 'light', 1440, 900], ['404', 'v2-404', 'light', 1440, 900],
    ['contact', 'v2-contact', 'light', 1440, 900], ['search', 'v2-search', 'light', 1440, 900],
    ['board-dark', 'v2-board', 'dark', 1440, 900], ['opportunity-dark', 'v2-opportunity', 'dark', 1440, 900], ['opportunities-dark', 'v2-opps', 'dark', 1440, 900],
    ['board-phone', 'v2-board', 'light', 390, 844], ['opportunity-phone', 'v2-opportunity', 'light', 390, 844], ['opportunities-phone', 'v2-opps', 'light', 390, 844],
  ].map(([n, key, scheme, w, h, full]) => ({ name: `rollout-${n}${scheme === 'dark' || w === 390 ? '' : '-light'}${w === 390 ? '-390' : w === 1440 && scheme === 'dark' ? '-1440' : '-1440'}`.replace('-light-1440-1440', '-light-1440'), raw: true, key, w, h, scheme, mobile: w === 390, full: !!full })),
];
const shots = allShots.filter(s => only === 'all' || (s.raw ? only === 'rollout' : s.name.startsWith(only)));
fs.mkdirSync(out, { recursive: true });
const browser = await chromium.launch();
for (const s of shots) {
  const file = find(s.key);
  if (!file) { console.error('missing dump for', s.key); process.exitCode = 1; continue; }
  const ctx = await browser.newContext({ viewport: { width: s.w, height: s.h }, deviceScaleFactor: 2, colorScheme: s.scheme, isMobile: !!s.mobile, hasTouch: !!s.mobile, reducedMotion: 'reduce' });
  const page = await ctx.newPage();
  await page.goto(`${base}/__page/${file}`, { waitUntil: 'load' });
  await page.addStyleTag({ content: '.env-banner { display: none !important; }' }); // the test-environment banner is not part of the design
  await page.waitForTimeout(s.key === 'v2-analytics' ? 800 : 150); // Chart.js draws after load
  const target = path.join(out, `${s.raw ? s.name : s.name.replace('-', '-' + prefix + '-')}.png`);
  if (s.raw && s.full) { // a viewport as tall as the page, so the sticky sidebar spans it like it does for a real reader
    const height = await page.evaluate('document.documentElement.scrollHeight');
    await page.setViewportSize({ width: s.w, height });
    await page.waitForTimeout(100);
  }
  await page.screenshot({ path: target, fullPage: !!s.full });
  console.log(target, fs.statSync(target).size);
  await ctx.close();
}
await browser.close();
server.close();
