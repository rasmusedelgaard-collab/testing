using Crm.Web.Config;
using Crm.Web.Data;
using Microsoft.Data.SqlClient;

namespace Crm.Tests;

/// <summary>
/// A fresh database crm_test_{guid}, migrated by the migrator as SA (the deployer), with a low-privilege SQL login/user
/// crm_app_test in crm_app_role (like create-app-user.sql). The app under test connects as crm_app_test, so grants and RLS are real.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string AppUser = "crm_app_test";
    private string _appLogin = "";
    private string _appPassword = "";

    public string DatabaseName { get; } = "crm_test_" + Guid.NewGuid().ToString("N");
    public DbConfig AppDb { get; private set; } = null!;
    public DbConfig OwnerDb { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!SqlEnv.ShouldRun)
        {
            return; // tests are skipped
        }
        var sa = SqlEnv.SaBuilder();
        _appLogin = AppUser + "_" + Guid.NewGuid().ToString("N")[..8];
        _appPassword = "Aa1-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        await using (var master = new SqlConnection(new SqlConnectionStringBuilder(sa.ConnectionString) { InitialCatalog = "master" }.ConnectionString))
        {
            await master.OpenAsync();
            await ExecAsync(master, $"CREATE DATABASE [{DatabaseName}]");
            await ExecAsync(master, $"CREATE LOGIN [{_appLogin}] WITH PASSWORD = '{_appPassword}', CHECK_POLICY = OFF");
        }
        OwnerDb = SqlEnv.ToDbConfig(sa, DatabaseName);
        var factory = new SqlConnectionFactory(OwnerDb);
        var migrator = new Migrator(factory, TextWriter.Null, Console.Error);
        Assert.Equal(0, await migrator.MigrateAsync(CancellationToken.None));
        Assert.Equal(0, await migrator.SeedTestAsync(CrmConfigLoader.LoadTestAdmin("Fixture Admin", "fixture.admin@example.com"), CancellationToken.None));
        await using (var db = new SqlConnection(new SqlConnectionStringBuilder(sa.ConnectionString) { InitialCatalog = DatabaseName }.ConnectionString))
        {
            await db.OpenAsync();
            await ExecAsync(db, $"CREATE USER [{AppUser}] FOR LOGIN [{_appLogin}]");
            await ExecAsync(db, $"ALTER ROLE crm_app_role ADD MEMBER [{AppUser}]");
        }
        AppDb = SqlEnv.ToDbConfig(sa, DatabaseName, _appLogin, _appPassword);
    }

    public async ValueTask DisposeAsync()
    {
        if (!SqlEnv.ShouldRun || OwnerDb is null)
        {
            return;
        }
        SqlConnection.ClearAllPools();
        var sa = SqlEnv.SaBuilder();
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(sa.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await master.OpenAsync();
        await ExecAsync(master, $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        await ExecAsync(master, $"DROP DATABASE [{DatabaseName}]");
        await ExecAsync(master, $"DROP LOGIN [{_appLogin}]");
    }

    public static async Task ExecAsync(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("sql")]
public sealed class SqlDbDefinition : ICollectionFixture<SqlServerFixture>;
