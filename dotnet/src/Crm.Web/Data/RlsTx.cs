using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Crm.Web.Data;

/// <summary>
/// The only handle repositories get. Every call runs on the request's transaction, one command at a time
/// (MARS is off; the semaphore serialises even a Task.WhenAll in page code, port of txQueue.ts).
/// </summary>
public sealed class RlsTx
{
    private readonly SqlConnection _conn;
    private readonly SqlTransaction _tx;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly int _commandTimeout;
    private readonly CancellationToken _ct;

    internal RlsTx(SqlConnection conn, SqlTransaction tx, int commandTimeoutSeconds, CancellationToken ct)
    {
        _conn = conn;
        _tx = tx;
        _commandTimeout = commandTimeoutSeconds;
        _ct = ct;
    }

    private CommandDefinition Cmd(string sql, object? param) =>
        new(sql, param, _tx, _commandTimeout, CommandType.Text, CommandFlags.Buffered, _ct);

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null)
    {
        await _one.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            return (await _conn.QueryAsync<T>(Cmd(sql, param)).ConfigureAwait(false)).AsList();
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Rows as dictionaries (column names as in the SELECT), for the generic entity SQL.</summary>
    public async Task<IReadOnlyList<Crm.Web.Domain.Row>> QueryRowsAsync(string sql, object? param = null)
    {
        await _one.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            var rows = await _conn.QueryAsync(Cmd(sql, param)).ConfigureAwait(false);
            return rows.Select(r => new Crm.Web.Domain.Row((IDictionary<string, object?>)r)).ToList();
        }
        finally
        {
            _one.Release();
        }
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null)
    {
        await _one.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            return await _conn.QuerySingleOrDefaultAsync<T>(Cmd(sql, param)).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        await _one.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            return await _conn.ExecuteAsync(Cmd(sql, param)).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    public async Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null)
    {
        await _one.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            return await _conn.ExecuteScalarAsync<T>(Cmd(sql, param)).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The grid reader is fully consumed and disposed inside the semaphore scope.</summary>
    public async Task<T> QueryMultipleAsync<T>(string sql, object? param, Func<SqlMapper.GridReader, Task<T>> read)
    {
        await _one.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            using var grid = await _conn.QueryMultipleAsync(Cmd(sql, param)).ConfigureAwait(false);
            return await read(grid).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    internal async Task CommitAsync()
    {
        await _one.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            await _tx.CommitAsync(_ct).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }
}
