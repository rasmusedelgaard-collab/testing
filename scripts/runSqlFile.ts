/**
 * Runs one T-SQL file (split on GO lines) on a single connection and prints every result row.
 * Used by the manual `rls_test` CI job for db/tests/rls-integration.sql:
 *   npm run db:run-sql -- db/tests/rls-integration.sql
 * A failing batch is reported with its SQL error number, line number (inside the batch), procedure and the offending
 * line, and the run CONTINUES with the next batch (the RLS script is built from small self-contained batches).
 * Exit code 1 if any batch errors or any row has Result = FAIL. Connection settings come from the DB_* env (never printed).
 */
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import sql from 'mssql';
import { ConfigError, loadDbConfig } from '../src/config';
import { buildSqlConfig } from '../src/db/sqlConfig';
import { splitBatches } from './sqlBatches';

/** Never includes connection settings; only SQL error metadata and the script's own text. */
export function describeBatchError(err: unknown, n: number, total: number, batch: string): string {
  const e = err as { message?: string; number?: number; lineNumber?: number; procName?: string; state?: number; originalError?: { info?: { number?: number; lineNumber?: number; procName?: string; state?: number } } };
  const info = e.originalError?.info ?? {};
  const number = e.number ?? info.number;
  const line = e.lineNumber ?? info.lineNumber;
  const proc = e.procName ?? info.procName;
  const text = typeof line === 'number' ? batch.split('\n')[line - 1]?.trim().slice(0, 200) : undefined;
  const first = batch.split('\n').find((l) => l.trim() !== '' && !l.trim().startsWith('--'))?.trim().slice(0, 100);
  return [
    `Batch ${n}/${total} FAILED: ${e.message ?? 'unknown error'}`,
    `  SQL error ${number ?? '?'}${e.state ?? info.state ? ` state ${e.state ?? info.state}` : ''}, line ${line ?? '?'} of the batch${proc ? `, in ${proc}` : ''}`,
    text ? `  at: ${text}` : '',
    first ? `  batch starts with: ${first}` : '',
  ].filter(Boolean).join('\n');
}

async function main(): Promise<number> {
  const file = process.argv[2];
  if (!file) throw new Error('usage: runSqlFile.ts <file.sql>');
  const text = await readFile(path.resolve(file), 'utf8');
  const db = loadDbConfig(process.env);
  // One connection: the script keeps state (transaction, table variables, session context) across batches.
  const pool = new sql.ConnectionPool(buildSqlConfig(db, { max: 1, min: 1 }));
  await pool.connect();
  let failed = 0;
  try {
    const batches = splitBatches(text);
    for (const [index, batch] of batches.entries()) {
      let res;
      try {
        res = await pool.request().batch(batch);
      } catch (err) {
        failed++;
        console.error(describeBatchError(err, index + 1, batches.length, batch));
        continue;
      }
      for (const rs of res.recordsets as unknown as Array<Array<Record<string, unknown>>>) {
        for (const row of rs) {
          const result = row['Result'];
          if (result !== undefined) {
            if (result !== 'PASS') failed++;
            console.log(`${String(row['Case']).padEnd(8)} ${String(result).padEnd(5)} ${String(row['Description'] ?? '')}${result === 'PASS' ? '' : ` | ${String(row['Detail'] ?? '')}`}`);
          } else {
            console.log(JSON.stringify(row));
          }
        }
      }
    }
  } finally {
    await pool.close();
  }
  if (failed > 0) {
    console.error(`${failed} case(s) FAILED`);
    return 1;
  }
  console.log('No FAIL rows.');
  return 0;
}

if (require.main === module) {
  main().then(
  (code) => process.exit(code),
  (err) => {
    console.error(err instanceof ConfigError ? err.message : `Error: ${(err as Error).message}`);
    process.exit(1);
  },
  );
}
