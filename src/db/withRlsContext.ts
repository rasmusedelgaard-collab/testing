import sql from 'mssql';
import { z } from 'zod';
import { logger } from '../logger';
import { mapDbError, STALE_CONTEXT } from './mapDbError';
import { getRlsPool } from './pool';
import { withDbRetry } from './retry';
import { sqlErrorNumber } from './transient';

export type Role = 'Admin' | 'Manager' | 'User';
export interface RlsContext {
  userId: number;
  role: Role;
  teamId: number | null;
}

const contextSchema = z.object({
  userId: z.number().int().positive(),
  role: z.enum(['Admin', 'Manager', 'User']),
  teamId: z.number().int().positive().nullable(),
});

/** Guard first: if pool reset ever failed, refuse rather than inherit another user's identity. */
export const SET_CONTEXT_BATCH = `
IF SESSION_CONTEXT(N'UserId') IS NOT NULL OR SESSION_CONTEXT(N'Role') IS NOT NULL
  THROW 51000, 'Stale RLS context on pooled connection', 1;
EXEC sp_set_session_context @key = N'UserId', @value = @UserId, @read_only = 1;
EXEC sp_set_session_context @key = N'Role',   @value = @Role,   @read_only = 1;
EXEC sp_set_session_context @key = N'TeamId', @value = @TeamId, @read_only = 1;
`;

async function rollbackQuietly(tx: sql.Transaction): Promise<void> {
  try {
    await tx.rollback();
  } catch {
    /* transaction already finished or connection gone: nothing left to undo */
  }
}

/** Begins a transaction and sets the context. Safe to repeat: nothing has been committed or run for the caller yet. */
async function beginWithContext(ctx: RlsContext): Promise<sql.Transaction> {
  const pool = await getRlsPool();
  const tx = new sql.Transaction(pool);
  try {
    await tx.begin(sql.ISOLATION_LEVEL.READ_COMMITTED);
    await new sql.Request(tx)
      .input('UserId', sql.Int, ctx.userId)
      .input('Role', sql.NVarChar(10), ctx.role)
      .input('TeamId', sql.Int, ctx.teamId)
      .query(SET_CONTEXT_BATCH);
    return tx;
  } catch (err) {
    await rollbackQuietly(tx); // also releases (and resets) the connection
    throw err;
  }
}

/**
 * The only way application code touches RLS-protected tables: one transaction per request with the
 * identity set as read-only session context. Do no external I/O inside `fn`, and render after it returns.
 * Once `fn` has started, transient errors are NOT retried (the outcome of a commit could be ambiguous).
 */
export async function withRlsContext<T>(ctx: RlsContext, fn: (tx: sql.Transaction) => Promise<T>): Promise<T> {
  const parsed = contextSchema.parse(ctx); // throws before any DB call
  let tx: sql.Transaction;
  try {
    tx = await withDbRetry(() => beginWithContext(parsed), { label: 'rls-context' });
  } catch (err) {
    if (sqlErrorNumber(err) === STALE_CONTEXT) logger.error({ userId: parsed.userId }, 'security: stale RLS context detected on pooled connection');
    throw mapDbError(err);
  }
  try {
    const result = await fn(tx);
    await tx.commit();
    return result;
  } catch (err) {
    await rollbackQuietly(tx);
    if (sqlErrorNumber(err) === STALE_CONTEXT) logger.error({ userId: parsed.userId }, 'security: stale RLS context detected');
    throw mapDbError(err);
  }
}
