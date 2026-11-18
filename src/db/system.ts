import type { ISqlType } from 'mssql';
import { withDbRetry } from './retry';
import { closePools, getSystemPool } from './pool';

export interface SqlParam {
  name: string;
  type: ISqlType | (() => ISqlType);
  value: unknown;
}

/**
 * Runs a statement on systemPool (no RLS context ever set). Only for idempotent work: session store,
 * login upsert, public reads. Retries transient errors, so the whole statement must be safe to repeat.
 */
export async function systemQuery<T = Record<string, unknown>>(text: string, params: SqlParam[] = [], opts: { retry?: boolean } = {}): Promise<T[]> {
  const run = async () => {
    const pool = await getSystemPool();
    const request = pool.request();
    for (const p of params) request.input(p.name, p.type, p.value);
    const result = await request.query(text);
    return result.recordset as unknown as T[];
  };
  const rows = opts.retry === false ? await run() : await withDbRetry(run, { label: 'system' });
  return rows ?? [];
}


/** Closes both pools (graceful shutdown). */
export const closeDatabase = closePools;
