// Builds the deployable folder ./package (what the `deploy` branch contains). Run via `npm run package`.
// Contents: dist/ (no source maps), package.json + lockfile, production node_modules, web.config, iisnode-entry.js,
// db/ scripts, crm.env.example. Never copies src/, tests/, .env* or any secret.
import { cp, mkdir, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.join(root, 'package');
const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm';

await stat(path.join(root, 'dist', 'server.js')).catch(() => {
  throw new Error('dist/server.js missing: run `npm run build` first (`npm run package` does this).');
});

await rm(out, { recursive: true, force: true });
await mkdir(out, { recursive: true });

await cp(path.join(root, 'dist'), path.join(out, 'dist'), { recursive: true, filter: (src) => !src.endsWith('.map') });
await cp(path.join(root, 'db'), path.join(out, 'db'), { recursive: true });
await cp(path.join(root, 'deploy', 'iis', 'web.config'), path.join(out, 'web.config'));
await cp(path.join(root, 'deploy', 'iis', 'iisnode-entry.js'), path.join(out, 'iisnode-entry.js'));
await cp(path.join(root, 'deploy', 'crm.env.example'), path.join(out, 'crm.env.example'));
await cp(path.join(root, 'package-lock.json'), path.join(out, 'package-lock.json'));

const pkg = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
const slim = { name: pkg.name, version: pkg.version, private: true, license: pkg.license, engines: pkg.engines, main: 'iisnode-entry.js', scripts: { start: 'node iisnode-entry.js' }, dependencies: pkg.dependencies };
await writeFile(path.join(out, 'package.json'), JSON.stringify(slim, null, 2) + '\n');

// Production dependencies only; --ignore-scripts so no dependency runs code at install time.
execFileSync(npm, ['ci', '--omit=dev', '--ignore-scripts', '--no-audit', '--no-fund'], { cwd: out, stdio: 'inherit' });

let sha = process.env.GITHUB_SHA ?? '';
if (!sha) {
  try {
    sha = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim();
  } catch {
    sha = 'unknown';
  }
}
await writeFile(path.join(out, 'BUILD-INFO.txt'), `version=${pkg.version}\ncommit=${sha}\nbuilt=${new Date().toISOString()}\n`);

// Safety net: refuse to produce a package containing anything that must not ship.
async function walk(dir, acc = []) {
  for (const e of await readdir(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (e.name === 'node_modules' && dir !== out) continue; // vendor trees are checked at top level only
      await walk(p, acc);
    } else acc.push(p);
  }
  return acc;
}
const bad = (await walk(out)).filter((f) => {
  const rel = path.relative(out, f).split(path.sep).join('/');
  if (rel.startsWith('node_modules/')) return false;
  return /(^|\/)\.env(\.|$)/.test(rel) || /(^|\/)crm\.env$/.test(rel) || rel.endsWith('.map') || rel.endsWith('.ts') || /^(src|tests)\//.test(rel);
});
if (bad.length) throw new Error(`Package contains forbidden files:\n${bad.join('\n')}`);
console.log(`Package ready in ${path.relative(root, out)}/ (version ${pkg.version}, commit ${sha.slice(0, 7)})`);
