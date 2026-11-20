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
];
const shots = allShots.filter(s => only === 'all' || s.name.startsWith(only));
fs.mkdirSync(out, { recursive: true });
const browser = await chromium.launch();
for (const s of shots) {
  const file = find(s.key);
  if (!file) { console.error('missing dump for', s.key); process.exitCode = 1; continue; }
  const ctx = await browser.newContext({ viewport: { width: s.w, height: s.h }, deviceScaleFactor: 2, colorScheme: s.scheme, isMobile: !!s.mobile, hasTouch: !!s.mobile, reducedMotion: 'reduce' });
  const page = await ctx.newPage();
  await page.goto(`${base}/__page/${file}`, { waitUntil: 'load' });
  await page.addStyleTag({ content: '.env-banner { display: none !important; }' }); // the test-environment banner is not part of the design
  await page.waitForTimeout(150);
  const target = path.join(out, `${s.name.replace('-', '-' + prefix + '-')}.png`);
  await page.screenshot({ path: target, fullPage: !!s.full });
  console.log(target, fs.statSync(target).size);
  await ctx.close();
}
await browser.close();
server.close();
