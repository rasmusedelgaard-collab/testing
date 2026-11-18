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
const strip = (b: string) => b.replace(/--[^\n]*/g, '');
const PROC_PARAMS = new Set(['@EntraOid', '@TenantId', '@DisplayName', '@Email', '@Role', '@GroupIdsJson', '@GroupsOverage']); // named parameters of crm.usp_UpsertUserOnLogin

describe('rls-integration.sql structure', () => {
  it('is a list of GO-separated batches (not one huge batch)', () => {
    expect(batches.length).toBeGreaterThan(100);
  });

  it('every variable used in a batch is declared in that same batch', () => {
    const problems: string[] = [];
    batches.forEach((raw, i) => {
      const b = strip(raw);
      const declared = new Set<string>();
      for (const m of b.matchAll(/DECLARE\b([^;]*);/gi)) for (const v of (m[1] as string).matchAll(/(@\w+)\s+(?:int|uniqueidentifier|nvarchar|varchar|TABLE)/gi)) declared.add(v[1] as string);
      const used = new Set([...b.matchAll(/(?<![@\w])@\w+/g)].map((m) => m[0]));
      for (const u of used) if (!declared.has(u) && !PROC_PARAMS.has(u)) problems.push(`batch ${i + 1}: ${u}`);
    });
    expect(problems).toEqual([]);
  });

  it('declared variables are declared before their first use inside the batch', () => {
    const late: string[] = [];
    batches.forEach((raw, i) => {
      const b = strip(raw);
      for (const m of b.matchAll(/DECLARE\b([^;]*);/gi)) {
        for (const v of (m[1] as string).matchAll(/(@\w+)\s+(?:int|uniqueidentifier|nvarchar|varchar|TABLE)/gi)) {
          const first = b.search(new RegExp(`(?<![@\\w])${v[1]}\\b`));
          if (first < (m.index ?? 0)) late.push(`batch ${i + 1}: ${v[1]}`);
        }
      }
    });
    expect(late).toEqual([]);
  });

  it('no cross-batch scratch table variables are used; ids travel through the #ids temp table', () => {
    expect(script).not.toMatch(/INSERT @R\b/);
    expect(strip(batches[0] as string)).toContain('CREATE TABLE #ids');
    expect(strip(batches[1] as string)).toContain('BEGIN TRAN;');
    expect(strip(batches[1] as string)).toMatch(/INSERT #ids \(K, Id\) VALUES/);
  });

  it('every case batch takes a savepoint, prints exactly one result row path, and undoes its writes', () => {
    const cases = batches.filter((b) => b.includes('SAVE TRANSACTION sp_case'));
    expect(cases.length).toBeGreaterThan(120);
    for (const b of cases) {
      expect(b).toMatch(/AS Result/);
      expect(b).toContain('BEGIN TRY');
      expect(b).toContain('END CATCH;');
      expect(b).toContain('IF XACT_STATE() = 1 ROLLBACK TRANSACTION sp_case;');
    }
  });

  it('the whole transaction is rolled back at the end and the read-only context case runs after it', () => {
    const last = batches.slice(-2).map(strip);
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
