using Dapper;
using Microsoft.Data.SqlClient;

namespace Crm.Web.Data;

/// <summary>
/// Statements that must run without RLS context (dotnet.md 6.4): sessions, login upsert, IsActive, branding, readyz,
/// test users. Opens a connection from the system pool per call, never sets context.
/// </summary>
public sealed class SystemDb(SqlConnectionFactory factory, DbRetry retry)
{
    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param, CancellationToken ct, bool retryTransient = true)
    {
        async Task<IReadOnlyList<T>> Run()
        {
            await using var c = await factory.OpenSystemAsync(ct).ConfigureAwait(false);
            return (await c.QueryAsync<T>(new CommandDefinition(sql, param, commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(false)).AsList();
        }
        return retryTransient ? await retry.RunAsync(Run, "system", ct).ConfigureAwait(false) : await Run().ConfigureAwait(false);
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param, CancellationToken ct, bool retryTransient = true)
    {
        var rows = await QueryAsync<T>(sql, param, ct, retryTransient).ConfigureAwait(false);
        return rows.Count == 0 ? default : rows.Single();
    }

    public async Task<int> ExecuteAsync(string sql, object? param, CancellationToken ct, bool retryTransient = true)
    {
        async Task<int> Run()
        {
            await using var c = await factory.OpenSystemAsync(ct).ConfigureAwait(false);
            return await c.ExecuteAsync(new CommandDefinition(sql, param, commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(false);
        }
        return retryTransient ? await retry.RunAsync(Run, "system", ct).ConfigureAwait(false) : await Run().ConfigureAwait(false);
    }

    /// <summary>Readiness probe: one cheap statement, no retry so callers get a fast answer.</summary>
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await QueryAsync<int>("SELECT 1", null, ct, retryTransient: false).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            return false;
        }
    }
}
