/**
 * Applies db/migrations/NNN_*.sql in order and records each in dbo.SchemaVersions with a SHA-256 checksum.
 * Refuses to continue if an already-applied file changed. Run as the deployer / DB owner identity, never as the app.
 *
 *   npm run db:migrate            apply pending migrations
 *   npm run db:migrate -- seed-test   run db/seed/test-users.sql (TEST data, interim test site only)
 */
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import sql from 'mssql';
import { ConfigError, loadDbConfig, loadTestAdmin } from '../src/config';
import { TEST_ADMIN_UPSERT_SQL, testAdminParams } from './testAdminSeed';
import { buildSqlConfig } from '../src/db/sqlConfig';
import { checksum, parseMigrationName, splitBatches } from './sqlBatches';

const root = path.resolve(__dirname, '..');

const BOOTSTRAP = `
IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL
  CREATE TABLE dbo.SchemaVersions (
    Version   int           NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
    Name      nvarchar(200) NOT NULL,
    Checksum  char(64)      NOT NULL,
    AppliedAt datetime2(3)  NOT NULL CONSTRAINT DF_SchemaVersions_AppliedAt DEFAULT SYSUTCDATETIME()
  );`;

async function runBatches(pool: sql.ConnectionPool, batches: string[], tx?: sql.Transaction) {
  for (const batch of batches) {
    await (tx ? new sql.Request(tx) : pool.request()).batch(batch);
  }
}

async function assertPoliciesEnabled(pool: sql.ConnectionPool): Promise<void> {
  const res = await pool.request().query(
    `SELECT name, is_enabled FROM sys.security_policies WHERE name IN (N'CrmRowPolicy', N'CrmConfigPolicy')`,
  );
  const enabled = new Set(res.recordset.filter((r: { is_enabled: boolean }) => r.is_enabled).map((r: { name: string }) => r.name));
  const missing = ['CrmRowPolicy', 'CrmConfigPolicy'].filter((n) => !enabled.has(n));
  if (missing.length > 0) {
    throw new Error(`Security policy not enabled or missing: ${missing.join(', ')}. Refusing to finish: data would be unprotected.`);
  }
  console.log('Security policies CrmRowPolicy and CrmConfigPolicy are enabled.');
}

async function migrate(pool: sql.ConnectionPool): Promise<void> {
  await pool.request().batch(BOOTSTRAP);
  const applied = new Map<number, string>();
  const rows = await pool.request().query('SELECT Version, Checksum FROM dbo.SchemaVersions');
  for (const r of rows.recordset as Array<{ Version: number; Checksum: string }>) applied.set(r.Version, r.Checksum.trim());

  const dir = path.join(root, 'db', 'migrations');
  const files = (await readdir(dir)).filter((f) => parseMigrationName(f)).sort();
  for (const file of files) {
    const meta = parseMigrationName(file)!;
    const text = await readFile(path.join(dir, file), 'utf8');
    const sum = checksum(text);
    const existing = applied.get(meta.version);
    if (existing !== undefined) {
      if (existing !== sum) throw new Error(`Migration ${file} was changed after it was applied (checksum mismatch). Add a new migration instead.`);
      console.log(`skip   ${file}`);
      continue;
    }
    console.log(`apply  ${file}`);
    const tx = new sql.Transaction(pool);
    await tx.begin();
    try {
      await runBatches(pool, splitBatches(text), tx);
      await new sql.Request(tx)
        .input('v', sql.Int, meta.version)
        .input('n', sql.NVarChar(200), meta.name)
        .input('c', sql.Char(64), sum)
        .query('INSERT INTO dbo.SchemaVersions (Version, Name, Checksum) VALUES (@v, @n, @c)');
      await tx.commit();
    } catch (err) {
      await tx.rollback().catch(() => undefined);
      throw new Error(`Migration ${file} failed and was rolled back: ${(err as Error).message}`, { cause: err });
    }
  }
  await assertPoliciesEnabled(pool);
}

async function seedTest(pool: sql.ConnectionPool): Promise<void> {
  const text = await readFile(path.join(root, 'db', 'seed', 'test-users.sql'), 'utf8');
  await runBatches(pool, splitBatches(text));
  console.log('Seeded test users and teams.');
  const admin = loadTestAdmin(process.env); // optional: TEST_ADMIN_NAME + TEST_ADMIN_EMAIL
  if (admin) {
    const p = testAdminParams(admin);
    await pool.request().input('oid', sql.UniqueIdentifier, p.oid).input('name', sql.NVarChar(200), p.name).input('email', sql.NVarChar(254), p.email).query(TEST_ADMIN_UPSERT_SQL);
    console.log('Seeded the named test Admin (see TEST_ADMIN_*; the test-login picker offers this user).');
  }
}

async function main() {
  const db = loadDbConfig(process.env);
  // One connection only: session context set in a batch must stay on the same connection.
  const pool = new sql.ConnectionPool(buildSqlConfig(db, { max: 1, min: 1 }));
  await pool.connect();
  try {
    if (process.argv[2] === 'seed-test') await seedTest(pool);
    else await migrate(pool);
  } finally {
    await pool.close();
  }
}

main().catch((err) => {
  console.error(err instanceof ConfigError ? err.message : (err as Error).message);
  process.exit(1);
});
