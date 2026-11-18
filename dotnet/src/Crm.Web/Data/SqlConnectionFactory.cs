using Crm.Web.Config;
using Microsoft.Data.SqlClient;

namespace Crm.Web.Data;

/// <summary>
/// Builds the connection strings once (never by concatenation) and opens connections. SqlClient pools by exact string,
/// so the different Application Name values give two separate pools (RLS and system).
/// </summary>
public sealed class SqlConnectionFactory
{
    private readonly string _rls;
    private readonly string _system;
    private readonly string _migrator;

    public SqlConnectionFactory(DbConfig db)
    {
        _rls = Build(db, "crm-rls", db.PoolMax, pooling: true, commandTimeout: 30);
        _system = Build(db, "crm-system", 4, pooling: true, commandTimeout: 30);
        _migrator = Build(db, "crm-migrator", 1, pooling: false, commandTimeout: 300);
    }

    public static string Build(DbConfig db, string appName, int maxPool, bool pooling, int commandTimeout)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = db.Port is { } p ? $"{db.Server},{p}" : db.Server,
            InitialCatalog = db.Database,
            ApplicationName = appName,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = db.TrustServerCertificate,
            MultipleActiveResultSets = false,
            Pooling = pooling,
            ConnectTimeout = 15,
            CommandTimeout = commandTimeout,
            ConnectRetryCount = 1,
            PersistSecurityInfo = false,
        };
        if (pooling)
        {
            b.MaxPoolSize = maxPool;
            b.MinPoolSize = 0;
        }
        switch (db.AuthMode)
        {
            case DbAuthMode.ManagedIdentity:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryManagedIdentity;
                break;
            case DbAuthMode.DefaultAzure:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
                break;
            default:
                b.UserID = db.User ?? "";
                b.Password = db.Password ?? "";
                break;
        }
        return b.ConnectionString;
    }

    internal async Task<SqlConnection> OpenRlsAsync(CancellationToken ct)
    {
        var c = new SqlConnection(_rls);
        try
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
            return c;
        }
        catch
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<SqlConnection> OpenSystemAsync(CancellationToken ct)
    {
        var c = new SqlConnection(_system);
        try
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
            return c;
        }
        catch
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Single non-pooled connection for the migrator commands.</summary>
    internal async Task<SqlConnection> OpenMigratorAsync(CancellationToken ct)
    {
        var c = new SqlConnection(_migrator);
        try
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
            return c;
        }
        catch
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
