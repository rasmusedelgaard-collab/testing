import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { describeBatchError } from '../scripts/runSqlFile';
import { splitBatches } from '../scripts/sqlBatches';

/**
 * Structural guard for db/tests/rls-integration.sql, which cannot run in CI without SQL Server. The first real run failed with
 * "Must declare the scalar variable @T1": variables must never be referenced outside the batch that declares them.
 */
const script = readFileSync(path.resolve(__dirname, '../db/tests/rls-integration.sql'), 'utf8');
const batches = splitBatches(script);
/** Blanks comments and string literals (keeps line structure) so variable scanning cannot be fooled by text. */
const noComments = (b: string) => b.replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/--[^\n]*/g, '');
const strip = (b: string) => noComments(b).replace(/N?'(?:[^']|'')*'/g, "''");
const PROC_PARAMS = new Set(['@entraoid', '@tenantid', '@displayname', '@email', '@role', '@groupidsjson', '@groupsoverage']); // named parameters of crm.usp_UpsertUserOnLogin

interface Declared { name: string; index: number }
/** Parses every DECLARE statement: names of the comma-separated declarations at nesting depth 0 (a TABLE (...) body has its own commas). */
function declarations(b: string): Declared[] {
  const found: Declared[] = [];
  for (const m of b.matchAll(/\bDECLARE\b/gi)) {
    let depth = 0;
    let start = (m.index ?? 0) + m[0].length;
    const flush = (end: number) => {
      const part = b.slice(start, end);
      const v = /^\s*(@\w+)/.exec(part);
      if (v) found.push({ name: (v[1] as string).toLowerCase(), index: start + part.indexOf(v[1] as string) });
    };
    for (let i = start; i <= b.length; i++) {
      const c = b[i];
      if (c === '(') depth++;
      else if (c === ')') depth--;
      else if ((c === ',' && depth === 0) || c === ';' || c === undefined) {
        flush(i);
        if (c !== ',') break;
        start = i + 1;
      }
    }
  }
  return found;
}
/** SQL Server variable names are case-insensitive, so everything is compared lower-cased. Globals (@@ROWCOUNT) are not variables. */
const usages = (b: string) => [...b.matchAll(/(?<![@\w])@\w+/g)].map((m) => ({ name: m[0].toLowerCase(), index: m.index ?? 0 }));
const caseBatches = batches.map((raw, i) => ({ raw, i, text: noComments(raw) })).filter((x) => x.raw.includes('SAVE TRANSACTION sp_case'));
const REVERT_LOOP = "WHILE USER_NAME() = N'rlstest_app' REVERT;";

describe('rls-integration.sql structure', () => {
  it('is a list of GO-separated batches (not one huge batch)', () => {
    expect(batches.length).toBeGreaterThan(100);
  });

  it('the declaration parser sees what it should (self-check of the guard)', () => {
    const d = declarations('DECLARE @a int = (SELECT 1), @B uniqueidentifier;\nDECLARE @P TABLE (X int, Y int);');
    expect(d.map((x) => x.name)).toEqual(['@a', '@b', '@p']);
  });

  it('every variable used in a batch is declared in that same batch, before its first use, and only once (case-insensitive)', () => {
    const problems: string[] = [];
    batches.forEach((raw, i) => {
      const b = strip(raw);
      const decl = declarations(b);
      const seen = new Map<string, number>();
      for (const d of decl) {
        if (seen.has(d.name)) problems.push(`batch ${i + 1}: ${d.name} declared twice (names are case-insensitive)`);
        seen.set(d.name, d.index);
      }
      const reported = new Set<string>();
      for (const u of usages(b)) {
        if (PROC_PARAMS.has(u.name) && /^\s*=/.test(b.slice(u.index + u.name.length))) continue; // EXEC proc @Param = value
        const at = seen.get(u.name);
        if (at === undefined && !reported.has(u.name)) problems.push(`batch ${i + 1}: ${u.name} used but not declared in this batch`);
        else if (at !== undefined && u.index < at && !reported.has(u.name)) problems.push(`batch ${i + 1}: ${u.name} used before its DECLARE`);
        reported.add(u.name);
      }
    });
    expect(problems).toEqual([]);
  });

  it('no cross-batch scratch table variables are used; ids travel through the #ids temp table', () => {
    expect(script).not.toMatch(/INSERT @R\b/);
    expect(strip(batches[0] as string)).toContain('CREATE TABLE #ids');
    expect(strip(batches[1] as string)).toContain('BEGIN TRAN;');
    expect(strip(batches[1] as string)).toMatch(/INSERT #ids \(K, Id\) VALUES/);
  });

  it('every case batch takes a savepoint, prints a result row, catches errors and undoes its writes', () => {
    expect(caseBatches.length).toBeGreaterThan(120);
    for (const { raw, i } of caseBatches) {
      expect(raw, `batch ${i + 1}`).toMatch(/AS Result/);
      expect(raw, `batch ${i + 1}`).toContain('END TRY BEGIN CATCH\n');
      expect(raw, `batch ${i + 1}`).toContain('IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;');
      expect(raw, `batch ${i + 1}: doomed transaction handled`).toMatch(/ELSE IF XACT_STATE\(\) = -1 BEGIN[\s\S]*?ROLLBACK TRANSACTION;[\s\S]*?'ABORT'/);
    }
  });

  it('impersonation is always reverted: guard first, in the CATCH, and before the rollback to the savepoint', () => {
    for (const { text, i } of caseBatches) {
      const n = `batch ${i + 1}`;
      const lines = text.split('\n').map((l) => l.trim()).filter(Boolean);
      expect(lines[0], `${n}: first statement drops a leaked impersonation`).toBe(REVERT_LOOP);
      // loading ids from #ids (tempdb) is impossible while impersonating, so it must come after the guard
      expect(text.indexOf(REVERT_LOOP)).toBeLessThan(text.indexOf('#ids') === -1 ? Infinity : text.indexOf('#ids'));
      const catchAt = text.indexOf('\nEND TRY BEGIN CATCH\n');
      const endCatchAt = text.indexOf('\nEND CATCH;');
      expect(catchAt, n).toBeGreaterThan(0);
      const tryPart = text.slice(0, catchAt);
      const catchPart = text.slice(catchAt, endCatchAt);
      const after = text.slice(endCatchAt);
      expect(catchPart, `${n}: CATCH reverts before anything else`).toMatch(/BEGIN CATCH\n\s*WHILE USER_NAME\(\) = N'rlstest_app' REVERT;/);
      expect(after.indexOf(REVERT_LOOP), `${n}: revert after END CATCH`).toBeGreaterThanOrEqual(0);
      expect(after.indexOf(REVERT_LOOP), `${n}: revert precedes the savepoint rollback`).toBeLessThan(after.indexOf('ROLLBACK TRANSACTION sp_case'));
      // normal path: every EXECUTE AS has its own REVERT inside the TRY block
      const executeAs = (tryPart.match(/\bEXECUTE AS USER\b/gi) ?? []).length;
      const reverts = (tryPart.match(/\bREVERT;/g) ?? []).length - (tryPart.match(/WHILE USER_NAME\(\) = N'rlstest_app' REVERT;/g) ?? []).length;
      expect(reverts, `${n}: REVERT count matches EXECUTE AS count`).toBe(executeAs);
      expect(text).not.toMatch(/EXECUTE AS (?:LOGIN|USER)\s*=\s*N'(?!rlstest_app')/i);
    }
  });

  it('a doomed or missing transaction does not cascade: case batches skip when no transaction is open', () => {
    for (const { text, i } of caseBatches) expect(text, `batch ${i + 1}`).toContain('IF @@TRANCOUNT = 0 BEGIN SELECT');
  });

  it('the test user can connect (GRANT CONNECT in setup) and only case batches impersonate', () => {
    expect(noComments(batches[1] as string)).toContain('GRANT CONNECT TO rlstest_app;');
    batches.forEach((raw, i) => {
      if (!raw.includes('SAVE TRANSACTION sp_case')) expect(strip(raw), `batch ${i + 1}`).not.toMatch(/EXECUTE AS/i);
    });
  });

  it('the whole transaction is rolled back at the end and the read-only context case runs after it', () => {
    const last = batches.slice(-2).map(noComments);
    expect(last[0]).toContain('ROLLBACK TRANSACTION;');
    expect(last[1]).toContain('15664');
    expect(script).not.toMatch(/^\s*COMMIT/im);
  });

  it('covers the cases of overview 5.5 plus the later additions', () => {
    const ids = new Set([...script.matchAll(/SELECT '(\d+[a-e]?)' AS \[Case\]/g)].map((m) => m[1]));
    for (const id of ['1', '1b', '2', '3', '4', '5', '6', '7', '8', '9', '10', '11', '12', '13', '14', '15', '15b', '16', '17', '18a', '18b', '18c', '18d', '18e', '19', '20']) expect(ids.has(id), `case ${id}`).toBe(true);
  });
});

describe('describeBatchError (runner diagnostics)', () => {
  it('reports number, line, procedure and the offending line, and never anything else', () => {
    const batch = 'DECLARE @x int;\nSELECT @y;\nSELECT 3;';
    const msg = describeBatchError({ message: 'Must declare the scalar variable "@y".', number: 137, lineNumber: 2, procName: '' , state: 2 }, 4, 150, batch);
    expect(msg).toContain('Batch 4/150 FAILED: Must declare the scalar variable "@y".');
    expect(msg).toContain('SQL error 137 state 2, line 2 of the batch');
    expect(msg).toContain('at: SELECT @y;');
    expect(msg).toContain('batch starts with: DECLARE @x int;');
  });
  it('reads the metadata from the driver error shape (originalError.info) too', () => {
    const msg = describeBatchError({ message: 'boom', originalError: { info: { number: 208, lineNumber: 1, procName: 'crm.usp_X' } } }, 1, 2, 'SELECT * FROM nope');
    expect(msg).toContain('SQL error 208, line 1 of the batch, in crm.usp_X');
  });
});
