'use strict';
/**
 * iisnode entry point. Loads configuration (including secrets) from a file that is NOT in git, then starts the app.
 * Needed because iisnode does not turn web.config appSettings into environment variables on plain IIS hosts.
 *
 * Lookup order (first file found wins; values already present in the real environment are never overridden):
 *   1. file named by CRM_ENV_FILE (if the host lets you set real environment variables)
 *   2. .\crm.env    (site folder: written by the deploy workflow from GitHub Secrets)
 *   3. ..\crm.env   (one level above the site folder: optional manual fallback)
 * Format: KEY=value per line, '#' comments, optional single/double quotes. Values are never logged.
 */
const fs = require('node:fs');
const path = require('node:path');

function parse(text) {
  const out = {};
  for (const raw of text.replace(/^\uFEFF/, '').split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    const eq = line.indexOf('=');
    if (eq < 1) continue;
    const key = line.slice(0, eq).trim();
    let val = line.slice(eq + 1).trim();
    if (val.length >= 2 && (val[0] === '"' || val[0] === "'") && val.endsWith(val[0])) val = val.slice(1, -1);
    out[key] = val;
  }
  return out;
}

const candidates = [process.env.CRM_ENV_FILE, path.join(__dirname, 'crm.env'), path.join(__dirname, '..', 'crm.env')].filter(Boolean);
const file = candidates.find((f) => {
  try {
    return fs.statSync(f).isFile();
  } catch {
    return false;
  }
});

if (file) {
  const vars = parse(fs.readFileSync(file, 'utf8'));
  for (const [k, v] of Object.entries(vars)) if (process.env[k] === undefined) process.env[k] = v;
  console.log(`config: loaded ${Object.keys(vars).length} settings from ${path.basename(file)}`);
} else {
  console.error('config: no crm.env found (looked for CRM_ENV_FILE, .\\crm.env, ..\\crm.env); relying on real environment variables');
}

require('./dist/server.js');
