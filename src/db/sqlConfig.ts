import type { config as SqlConfig } from 'mssql';
import type { DbConfig } from '../config';

/** Builds the mssql config. Shared by the app pools and scripts/migrate.ts. */
export function buildSqlConfig(db: DbConfig, opts: { max: number; min?: number }): SqlConfig {
  const base: SqlConfig = {
    server: db.server,
    ...(db.port ? { port: db.port } : {}),
    database: db.database,
    options: { encrypt: true, trustServerCertificate: db.trustServerCertificate },
    // min 0: no warm connections, so a serverless database can auto-pause (overview 8).
    pool: { min: opts.min ?? 0, max: opts.max, idleTimeoutMillis: 30000 },
    connectionTimeout: 30000,
    requestTimeout: 30000,
  };
  switch (db.authMode) {
    case 'managed-identity':
      return { ...base, authentication: { type: 'azure-active-directory-msi-app-service', options: {} } };
    case 'default-azure':
      return { ...base, authentication: { type: 'azure-active-directory-default', options: {} } };
    case 'sql-password':
      return { ...base, user: db.user, password: db.password };
  }
}
