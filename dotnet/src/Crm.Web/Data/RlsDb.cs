using System.Data;
using Crm.Web.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Crm.Web.Data;

public interface IRlsDb
{
    /// <summary>One call per request: everything the page needs is collected inside fn; the page renders after commit.</summary>
    Task<T> RunAsync<T>(Func<RlsTx, Task<T>> fn, CancellationToken ct);
}

/// <summary>Scoped (per request). Port of withRlsContext: one connection + one transaction, context set in the first batch.</summary>
public sealed class RlsDb(SqlConnectionFactory factory, DbRetry retry, IRlsContextAccessor accessor, ILogger<RlsDb> log) : IRlsDb
{
    internal const string ContextBatch = """
        IF SESSION_CONTEXT(N'UserId') IS NOT NULL OR SESSION_CONTEXT(N'Role') IS NOT NULL
          THROW 51000, 'Stale RLS context on pooled connection', 1;
        EXEC sp_set_session_context @key = N'UserId', @value = @UserId, @read_only = 1;
        EXEC sp_set_session_context @key = N'Role',   @value = @Role,   @read_only = 1;
        EXEC sp_set_session_context @key = N'TeamId', @value = @TeamId, @read_only = 1;
        """;

    private bool _used;

    public Task<T> RunAsync<T>(Func<RlsTx, Task<T>> fn, CancellationToken ct) => RunAsync(accessor.Current, fn, ct);

    public async Task<T> RunAsync<T>(RlsContext context, Func<RlsTx, Task<T>> fn, CancellationToken ct)
    {
        context.Validate();
        if (_used)
        {
            throw new InvalidOperationException("RlsDb.RunAsync may be called only once per request (one connection + one transaction).");
        }
        _used = true;

        SqlConnection? conn = null;
        SqlTransaction? tx = null;
        try
        {
            (conn, tx) = await retry.RunAsync(async () =>
            {
                SqlConnection? c = null;
                SqlTransaction? t = null;
                try
                {
                    c = await factory.OpenRlsAsync(ct).ConfigureAwait(false);
                    t = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
                    await SetContextAsync(c, t, context, ct).ConfigureAwait(false);
                    return (c, t);
                }
                catch
                {
                    if (t is not null) { await t.DisposeAsync().ConfigureAwait(false); }
                    if (c is not null) { await c.DisposeAsync().ConfigureAwait(false); }
                    throw;
                }
            }, "rls", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Map(ex, context);
        }

        await using (conn.ConfigureAwait(false))
        await using (tx.ConfigureAwait(false))
        {
            var rtx = new RlsTx(conn, tx, 30, ct);
            try
            {
                var result = await fn(rtx).ConfigureAwait(false);
                await rtx.CommitAsync().ConfigureAwait(false);
                return result;
            }
            catch (Exception ex)
            {
                try
                {
                    if (tx.Connection is not null)
                    {
                        await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception rollbackEx)
                {
                    log.LogError("rollback failed ({Type}); connection discarded", rollbackEx.GetType().Name);
                    SqlConnection.ClearPool(conn);
                }
                throw Map(ex, context);
            }
        }
    }

    private static async Task SetContextAsync(SqlConnection c, SqlTransaction t, RlsContext ctx, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(ContextBatch, c, t);
        cmd.Parameters.Add(new SqlParameter("@UserId", SqlDbType.Int) { Value = ctx.UserId });
        cmd.Parameters.Add(new SqlParameter("@Role", SqlDbType.NVarChar, 10) { Value = ctx.Role.ToString() });
        cmd.Parameters.Add(new SqlParameter("@TeamId", SqlDbType.Int) { Value = (object?)ctx.TeamId ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private Exception Map(Exception ex, RlsContext ctx)
    {
        if (Transient.HasNumber(ex, DbErrorMapper.StaleContext))
        {
            log.LogError("SECURITY: stale RLS context on pooled connection (user {UserId})", ctx.UserId);
        }
        return DbErrorMapper.Map(ex);
    }
}
