"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.SET_CONTEXT_BATCH = void 0;
exports.withRlsContext = withRlsContext;
const mssql_1 = __importDefault(require("mssql"));
const zod_1 = require("zod");
const logger_1 = require("../logger");
const mapDbError_1 = require("./mapDbError");
const pool_1 = require("./pool");
const retry_1 = require("./retry");
const transient_1 = require("./transient");
const contextSchema = zod_1.z.object({
    userId: zod_1.z.number().int().positive(),
    role: zod_1.z.enum(['Admin', 'Manager', 'User']),
    teamId: zod_1.z.number().int().positive().nullable(),
});
/** Guard first: if pool reset ever failed, refuse rather than inherit another user's identity. */
exports.SET_CONTEXT_BATCH = `
IF SESSION_CONTEXT(N'UserId') IS NOT NULL OR SESSION_CONTEXT(N'Role') IS NOT NULL
  THROW 51000, 'Stale RLS context on pooled connection', 1;
EXEC sp_set_session_context @key = N'UserId', @value = @UserId, @read_only = 1;
EXEC sp_set_session_context @key = N'Role',   @value = @Role,   @read_only = 1;
EXEC sp_set_session_context @key = N'TeamId', @value = @TeamId, @read_only = 1;
`;
/**
 * Rolls back. If the rollback itself fails while the transaction still holds its connection (for example because a
 * request was still in flight), the connection is in an unknown state: close it and hand it back to the pool, which then
 * destroys it. The connection is therefore ALWAYS released, never leaked.
 */
async function rollbackQuietly(tx) {
    try {
        await tx.rollback();
        return;
    }
    catch {
        /* fall through: either already finished (nothing to release) or stuck (discard below) */
    }
    const internals = tx;
    const conn = internals._acquiredConnection;
    if (!conn)
        return;
    internals._acquiredConnection = null;
    internals._activeRequest = null;
    try {
        conn.close();
    }
    catch {
        /* already closed */
    }
    try {
        const pool = await (0, pool_1.getRlsPool)();
        pool.release(conn);
    }
    catch (err) {
        logger_1.logger.error({ err: err.message }, 'could not release a discarded connection');
    }
}
/** Begins a transaction and sets the context. Safe to repeat: nothing has been committed or run for the caller yet. */
async function beginWithContext(ctx) {
    const pool = await (0, pool_1.getRlsPool)();
    const tx = new mssql_1.default.Transaction(pool);
    try {
        await tx.begin(mssql_1.default.ISOLATION_LEVEL.READ_COMMITTED);
        await new mssql_1.default.Request(tx)
            .input('UserId', mssql_1.default.Int, ctx.userId)
            .input('Role', mssql_1.default.NVarChar(10), ctx.role)
            .input('TeamId', mssql_1.default.Int, ctx.teamId)
            .query(exports.SET_CONTEXT_BATCH);
        return tx;
    }
    catch (err) {
        await rollbackQuietly(tx); // also releases (and resets) the connection
        throw err;
    }
}
/**
 * The only way application code touches RLS-protected tables: one transaction per request with the
 * identity set as read-only session context. Do no external I/O inside `fn`, and render after it returns.
 * Once `fn` has started, transient errors are NOT retried (the outcome of a commit could be ambiguous).
 */
async function withRlsContext(ctx, fn) {
    const parsed = contextSchema.parse(ctx); // throws before any DB call
    let tx;
    try {
        tx = await (0, retry_1.withDbRetry)(() => beginWithContext(parsed), { label: 'rls-context' });
    }
    catch (err) {
        if ((0, transient_1.sqlErrorNumber)(err) === mapDbError_1.STALE_CONTEXT)
            logger_1.logger.error({ userId: parsed.userId }, 'security: stale RLS context detected on pooled connection');
        throw (0, mapDbError_1.mapDbError)(err);
    }
    try {
        const result = await fn(tx);
        await tx.commit();
        return result;
    }
    catch (err) {
        await rollbackQuietly(tx);
        if ((0, transient_1.sqlErrorNumber)(err) === mapDbError_1.STALE_CONTEXT)
            logger_1.logger.error({ userId: parsed.userId }, 'security: stale RLS context detected');
        throw (0, mapDbError_1.mapDbError)(err);
    }
}
//# sourceMappingURL=withRlsContext.js.map