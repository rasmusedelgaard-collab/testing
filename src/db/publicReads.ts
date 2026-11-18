import { systemQuery } from './system';

/** Readiness probe: one cheap statement, deliberately without retry so callers get a fast answer. */
export async function pingDatabase(): Promise<boolean> {
  try {
    await systemQuery('SELECT 1 AS ok', [], { retry: false });
    return true;
  } catch {
    return false;
  }
}
