/**
 * Runs one T-SQL file (split on GO lines) on a single connection and prints every result row.
 * Used by the manual `rls_test` CI job for db/tests/rls-integration.sql:
 *   npm run db:run-sql -- db/tests/rls-integration.sql
 * Exit code 1 if any batch errors or any row has Result = FAIL. Connection settings come from the DB_* env (never printed).
 */
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import sql from 'mssql';
import { ConfigError, loadDbConfig } from '../src/config';
import { buildSqlConfig } from '../src/db/sqlConfig';
import { splitBatches } from './sqlBatches';

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
    for (const batch of splitBatches(text)) {
      const res = await pool.request().batch(batch);
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

main().then(
  (code) => process.exit(code),
  (err) => {
    console.error(err instanceof ConfigError ? err.message : `Error: ${(err as Error).message}`);
    process.exit(1);
  },
);
