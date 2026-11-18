import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { ESLint } from 'eslint';
import { describe, expect, it } from 'vitest';

const root = path.resolve(__dirname, '..');
const srcDir = path.join(root, 'src');

function walk(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);
    return statSync(full).isDirectory() ? walk(full) : full.endsWith('.ts') ? [full] : [];
  });
}
const rel = (f: string) => path.relative(root, f).split(path.sep).join('/');
const files = walk(srcDir).map((f) => ({ file: rel(f), text: readFileSync(f, 'utf8') }));
const outside = (dir: string) => files.filter((f) => !f.file.startsWith(dir));
// Static imports, re-exports, dynamic import() and require(), with or without a .js/.ts suffix.
const SPEC = String.raw`\s*['"]`;
const POOL_IMPORT = new RegExp(String.raw`(?:from|import\s*\(|require\s*\()${SPEC}(?:[^'"]*/)?pool(?:\.[jt]s)?['"]`);
const MSSQL_IMPORT = new RegExp(String.raw`(?:from|import\s*\(|require\s*\()${SPEC}mssql(?:/[^'"]*)?['"]`);
const offenders = (list: typeof files, re: RegExp) => list.filter((f) => re.test(f.text)).map((f) => f.file);

describe('data-access guardrails (overview section 2)', () => {
  it('finds source files to check', () => {
    expect(files.length).toBeGreaterThan(10);
  });

  it('no raw SQL execution (new sql.Request, .query(, .batch() outside src/db/', () => {
    const out = outside('src/db/');
    expect(offenders(out, /new\s+sql\.Request\s*\(/)).toEqual([]);
    expect(offenders(out, /\.query\s*\(/)).toEqual([]);
    expect(offenders(out, /\.batch\s*\(/)).toEqual([]);
  });

  it("only src/db/ imports the 'mssql' driver", () => {
    expect(offenders(outside('src/db/'), MSSQL_IMPORT)).toEqual([]);
  });

  it('only withRlsContext.ts and system.ts import db/pool', () => {
    const importers = files.filter((f) => POOL_IMPORT.test(f.text)).map((f) => f.file).sort();
    expect(importers).toEqual(['src/db/system.ts', 'src/db/withRlsContext.ts']);
  });

  it('the rls pool accessor is referenced only by pool.ts and withRlsContext.ts', () => {
    expect(offenders(files, /\bgetRlsPool\b/).sort()).toEqual(['src/db/pool.ts', 'src/db/withRlsContext.ts']);
  });

  it('the system pool accessor is referenced only by pool.ts and system.ts', () => {
    expect(offenders(files, /\bgetSystemPool\b/).sort()).toEqual(['src/db/pool.ts', 'src/db/system.ts']);
  });

  it('process.env is read only in config.ts', () => {
    expect(offenders(files, /process\.env/)).toEqual(['src/config.ts']);
  });
});

describe('import guard patterns', () => {
  it.each([
    "import { x } from '../db/pool';", "import { x } from './pool.js';", "const m = await import('../db/pool.js');", "const m = require('./pool');", "export * from './pool';",
  ])('POOL_IMPORT matches %s', (code) => expect(POOL_IMPORT.test(code)).toBe(true));
  it.each(["import sql from 'mssql';", "const sql = require('mssql');", "await import('mssql')", "import x from 'mssql/lib/base';"])('MSSQL_IMPORT matches %s', (code) => expect(MSSQL_IMPORT.test(code)).toBe(true));
  it('does not match unrelated modules', () => {
    expect(POOL_IMPORT.test("import { x } from './carpool';")).toBe(false);
    expect(MSSQL_IMPORT.test("import x from 'mssql-tools';")).toBe(false);
  });
});

describe('ESLint no-restricted-imports', () => {
  const eslint = new ESLint({ cwd: root });
  const lint = async (code: string, filePath: string) => {
    const [res] = await eslint.lintText(code, { filePath: path.join(root, filePath) });
    return res!.messages.filter((m) => m.ruleId === 'no-restricted-imports');
  };

  it('blocks importing the pool from a route, auth module or repository', async () => {
    expect(await lint("import { getRlsPool } from '../db/pool';\nvoid getRlsPool;", 'src/routes/x.ts')).toHaveLength(1);
    expect(await lint("import { getSystemPool } from './pool';\nvoid getSystemPool;", 'src/db/sessionRepo.ts')).toHaveLength(1);
    expect(await lint("import { getSystemPool } from '../db/pool';\nvoid getSystemPool;", 'src/auth/sessionStore.ts')).toHaveLength(1);
  });

  it('allows withRlsContext.ts and system.ts', async () => {
    expect(await lint("import { getRlsPool } from './pool';\nvoid getRlsPool;", 'src/db/withRlsContext.ts')).toHaveLength(0);
    expect(await lint("import { getSystemPool } from './pool';\nvoid getSystemPool;", 'src/db/system.ts')).toHaveLength(0);
  });

  it('blocks the mssql driver outside src/db', async () => {
    expect(await lint("import sql from 'mssql';\nvoid sql;", 'src/routes/x.ts')).toHaveLength(1);
    expect(await lint("import sql from 'mssql';\nvoid sql;", 'src/db/sessionRepo.ts')).toHaveLength(0);
  });
});
