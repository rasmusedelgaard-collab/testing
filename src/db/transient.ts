/** Error numbers that mean "the database is (re)starting or briefly unavailable" (overview 8.1). */
const TRANSIENT_NUMBERS = new Set([40613, 40197, 40501, 40540, 49918, 49919, 49920, 4221, 10928, 10929]);
const TRANSIENT_CODES = new Set(['ETIMEOUT', 'ESOCKET', 'ECONNRESET', 'ECONNCLOSED']);

interface MaybeSqlError {
  number?: unknown;
  code?: unknown;
  originalError?: MaybeSqlError & { info?: { number?: unknown } };
  precedingErrors?: MaybeSqlError[];
  cause?: unknown;
}

/** Extracts the SQL Server error number from mssql/tedious error shapes, or undefined. */
export function sqlErrorNumber(err: unknown): number | undefined {
  const e = err as MaybeSqlError | null;
  if (!e || typeof e !== 'object') return undefined;
  const candidates = [e.number, e.originalError?.number, e.originalError?.info?.number];
  for (const c of candidates) if (typeof c === 'number') return c;
  if (e.cause && e.cause !== err) return sqlErrorNumber(e.cause);
  return undefined;
}

export function errorCode(err: unknown): string | undefined {
  const e = err as MaybeSqlError | null;
  if (!e || typeof e !== 'object') return undefined;
  if (typeof e.code === 'string') return e.code;
  if (typeof e.originalError?.code === 'string') return e.originalError.code;
  return undefined;
}

export function isTransient(err: unknown): boolean {
  const n = sqlErrorNumber(err);
  if (n !== undefined) return TRANSIENT_NUMBERS.has(n);
  const code = errorCode(err);
  if (code !== undefined && TRANSIENT_CODES.has(code)) return true;
  const e = err as MaybeSqlError | null;
  const preceding = e?.precedingErrors;
  if (Array.isArray(preceding)) return preceding.some((p) => isTransient(p));
  return false;
}
