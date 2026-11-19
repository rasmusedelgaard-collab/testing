using System.Globalization;
using Crm.Web.Config;
using Crm.Web.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crm.Tests.Sql;

/// <summary>A throw-away database of its own (migrated, seed-test applied), so demo rows never leak into the shared fixture database.</summary>
public sealed class DemoDb : IAsyncDisposable
{
    private string _login = "";

    public string Name { get; } = "crm_demo_" + Guid.NewGuid().ToString("N");
    public DbConfig OwnerDb { get; private set; } = null!;
    public DbConfig AppDb { get; private set; } = null!;

    public static async Task<DemoDb> CreateAsync()
    {
        var db = new DemoDb();
        var sa = SqlEnv.SaBuilder();
        db._login = "crm_demo_" + Guid.NewGuid().ToString("N")[..8];
        var password = "Aa1-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        await using (var master = new SqlConnection(new SqlConnectionStringBuilder(sa.ConnectionString) { InitialCatalog = "master" }.ConnectionString))
        {
            await master.OpenAsync();
            await SqlServerFixture.ExecAsync(master, $"CREATE DATABASE [{db.Name}]");
            await SqlServerFixture.ExecAsync(master, $"CREATE LOGIN [{db._login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF");
        }
        db.OwnerDb = SqlEnv.ToDbConfig(sa, db.Name);
        var migrator = db.Migrator(TextWriter.Null);
        Assert.Equal(0, await migrator.MigrateAsync(CancellationToken.None));
        Assert.Equal(0, await migrator.SeedTestAsync(null, CancellationToken.None));
        await using (var c = new SqlConnection(new SqlConnectionStringBuilder(sa.ConnectionString) { InitialCatalog = db.Name }.ConnectionString))
        {
            await c.OpenAsync();
            await SqlServerFixture.ExecAsync(c, $"CREATE USER [demo_app] FOR LOGIN [{db._login}]");
            await SqlServerFixture.ExecAsync(c, "ALTER ROLE crm_app_role ADD MEMBER [demo_app]");
        }
        db.AppDb = SqlEnv.ToDbConfig(sa, db.Name, db._login, password);
        return db;
    }

    public Migrator Migrator(TextWriter output) => new(new SqlConnectionFactory(OwnerDb), output, Console.Error);

    public async Task<int> SeedAsync(bool reset, TextWriter? output = null) => await Migrator(output ?? TextWriter.Null).SeedDemoAsync(reset, TimeProvider.System, CancellationToken.None);

    /// <summary>Runs a query as the owner with an Admin session context (RLS applies to dbo too), returning the first column of the first row.</summary>
    public async Task<T> ScalarAsync<T>(string sql, params (string, object)[] args)
    {
        await using var c = new SqlConnection(SqlConnectionFactory.Build(OwnerDb, "demo-test", 1, false, 60));
        await c.OpenAsync();
        await using var cmd = new SqlCommand("EXEC sp_set_session_context N'UserId', 0; EXEC sp_set_session_context N'Role', N'Admin'; " + sql, c);
        foreach (var (k, v) in args) { cmd.Parameters.AddWithValue(k, v); }
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T), CultureInfo.InvariantCulture);
    }

    public Task<int> CountAsync(string sql) => ScalarAsync<int>(sql);

    private const string DemoUsers = "(SELECT UserId FROM crm.Users WHERE LOWER(CONVERT(char(36), EntraOid)) LIKE '00000000-0000-4000-8000-00000000d0%')";

    public async Task<int[]> DemoCountsAsync() =>
    [
        await CountAsync($"SELECT COUNT(*) FROM crm.Accounts WHERE CreatedBy IN {DemoUsers}"),
        await CountAsync($"SELECT COUNT(*) FROM crm.Contacts WHERE CreatedBy IN {DemoUsers}"),
        await CountAsync($"SELECT COUNT(*) FROM crm.Opportunities WHERE CreatedBy IN {DemoUsers}"),
        await CountAsync($"SELECT COUNT(*) FROM crm.Activities WHERE CreatedBy IN {DemoUsers}"),
        await CountAsync($"SELECT COUNT(*) FROM crm.FxRates WHERE ModifiedBy IN {DemoUsers}"),
    ];

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(SqlEnv.SaBuilder().ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await master.OpenAsync();
        await SqlServerFixture.ExecAsync(master, $"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        await SqlServerFixture.ExecAsync(master, $"DROP DATABASE [{Name}]");
        await SqlServerFixture.ExecAsync(master, $"DROP LOGIN [{_login}]");
    }
}

[Collection("sql")]
public sealed class DemoSeedTests
{
    private static readonly int[] Expected = [40, 100, 180, 250, 60];

    [SqlFact]
    public async Task Seeding_twice_is_idempotent_and_rls_integration_still_passes()
    {
        await using var db = await DemoDb.CreateAsync();
        var first = new StringWriter();
        Assert.Equal(0, await db.SeedAsync(false, first));
        Assert.Contains("Seeded demo data", first.ToString(), StringComparison.Ordinal);
        Assert.Equal(Expected, await db.DemoCountsAsync());
        var audit = await db.CountAsync("SELECT COUNT(*) FROM crm.AuditLog");
        var lastId = await db.ScalarAsync<int>("SELECT MAX(AccountId) FROM crm.Accounts");
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey = 'demo.seeded'"));

        var second = new StringWriter();
        Assert.Equal(0, await db.SeedAsync(false, second));
        Assert.Contains("already present", second.ToString(), StringComparison.Ordinal);
        Assert.Equal(Expected, await db.DemoCountsAsync());
        Assert.Equal(audit, await db.CountAsync("SELECT COUNT(*) FROM crm.AuditLog"));
        Assert.Equal(lastId, await db.ScalarAsync<int>("SELECT MAX(AccountId) FROM crm.Accounts"));
        Assert.Equal(17, await db.CountAsync("SELECT COUNT(*) FROM crm.Users")); // 7 test users + 10 demo users
    }

    [SqlFact]
    public async Task Audit_and_ownership_name_the_real_owners_not_a_system_user()
    {
        await using var db = await DemoDb.CreateAsync();
        Assert.Equal(0, await db.SeedAsync(false));
        Assert.Equal(0, await db.CountAsync("SELECT COUNT(*) FROM crm.Accounts WHERE CreatedBy <> OwnerId OR ModifiedBy <> OwnerId"));
        Assert.Equal(0, await db.CountAsync("SELECT COUNT(*) FROM crm.Activities WHERE CreatedBy <> OwnerId"));
        Assert.True(await db.CountAsync("SELECT COUNT(*) FROM crm.AuditLog") > 500);
        Assert.Equal(0, await db.CountAsync("SELECT COUNT(*) FROM crm.AuditLog WHERE ChangedBy <> OwnerId"));
        Assert.Equal(0, await db.CountAsync("SELECT COUNT(*) FROM crm.AuditLog a JOIN crm.Users u ON u.UserId = a.ChangedBy WHERE u.Role = 'Admin'"));
        // Rows carry their owner's team; the admin owns nothing.
        Assert.Equal(0, await db.CountAsync("SELECT COUNT(*) FROM crm.Opportunities o JOIN crm.Users u ON u.UserId = o.OwnerId WHERE ISNULL(o.TeamId, -1) <> ISNULL(u.TeamId, -1)"));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Users WHERE IsActive = 0"));
        Assert.Equal(3, await db.CountAsync("SELECT COUNT(*) FROM crm.Opportunities o JOIN crm.Users u ON u.UserId = o.OwnerId WHERE u.IsActive = 0"));
        Assert.Equal(3, await db.CountAsync("SELECT COUNT(*) FROM crm.PicklistValues WHERE Code IN ('wind', 'building', 'industry')"));
        Assert.True(await db.CountAsync("SELECT COUNT(*) FROM crm.AuditLog WHERE Action = 'U' AND FieldName = 'StageId'") > 100);
        Assert.True(await db.CountAsync("SELECT COUNT(*) FROM crm.AuditLog WHERE Action = 'U' AND FieldName = 'OwnerId'") > 5);
        Assert.Equal(3, await db.CountAsync("SELECT COUNT(*) FROM crm.Teams WHERE LOWER(CONVERT(char(36), EntraGroupId)) LIKE '00000000-0000-4000-8000-0000000fd0%'"));
    }

    [SqlFact]
    public async Task Demo_users_appear_in_the_test_login_picker()
    {
        await using var db = await DemoDb.CreateAsync();
        Assert.Equal(0, await db.SeedAsync(false));
        var repo = new TestUserRepo(new SystemDb(new SqlConnectionFactory(db.AppDb), new DbRetry(TimeProvider.System, NullLogger<DbRetry>.Instance)));
        var users = await repo.ListAsync(CancellationToken.None);
        Assert.Contains(users, u => u.Name == "Astrid Holm" && u.Role == CrmRole.Admin);
        Assert.Equal(3, users.Count(u => u.Role == CrmRole.Manager && u.Name != "Test Manager A" && u.Name != "Test Manager B"));
        Assert.Equal(16, users.Count); // the inactive demo user is not offered
        Assert.Contains(users, u => u.TeamName == "Nordics");
        Assert.DoesNotContain(users, u => u.Name == "Peter Juhl");
        Assert.Contains(users, u => u.Name == "Lise Bech" && u.TeamId is null);
    }

    [SqlFact]
    public async Task Reset_removes_only_demo_rows_and_reseeds()
    {
        await using var db = await DemoDb.CreateAsync();
        Assert.Equal(0, await db.SeedAsync(false));
        var firstAccountId = await db.ScalarAsync<int>("SELECT MIN(AccountId) FROM crm.Accounts");

        // Real (non-demo) rows made by a real test user, including one that points at a demo account.
        var ctx = "DECLARE @u int = (SELECT UserId FROM crm.Users WHERE EntraOid = '00000000-0000-4000-8000-00000000a002'), @t int = (SELECT TeamId FROM crm.Users WHERE EntraOid = '00000000-0000-4000-8000-00000000a002'); ";
        await db.ScalarAsync<int>(ctx + """
            INSERT crm.Accounts (Name, OwnerId, TeamId, CreatedBy, ModifiedBy) VALUES (N'Real Customer A/S', @u, @t, @u, @u);
            DECLARE @acc int = SCOPE_IDENTITY();
            INSERT crm.Contacts (AccountId, LastName, OwnerId, TeamId, CreatedBy, ModifiedBy) VALUES (@acc, N'Real', @u, @t, @u, @u);
            INSERT crm.Opportunities (Name, AccountId, StageId, Amount, CurrencyCode, CloseDate, OwnerId, TeamId, CreatedBy, ModifiedBy)
              VALUES (N'Real deal', @acc, (SELECT TOP 1 StageId FROM crm.PipelineStages WHERE StageType = 'open' ORDER BY SortOrder), 1000, 'DKK', '2030-01-01', @u, @t, @u, @u);
            INSERT crm.Activities (ActivityType, Subject, AccountId, OwnerId, TeamId, CreatedBy, ModifiedBy) VALUES ('note', N'Real note', @acc, @u, @t, @u, @u);
            INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) VALUES ('DKK', 'NOK', '2030-01-01', 0.65, @u);
            SELECT 1;
            """);
        var realBefore = await db.CountAsync("SELECT COUNT(*) FROM crm.Accounts WHERE Name = N'Real Customer A/S'")
            + await db.CountAsync("SELECT COUNT(*) FROM crm.Contacts WHERE LastName = N'Real'")
            + await db.CountAsync("SELECT COUNT(*) FROM crm.Opportunities WHERE Name = N'Real deal'")
            + await db.CountAsync("SELECT COUNT(*) FROM crm.Activities WHERE Subject = N'Real note'")
            + await db.CountAsync("SELECT COUNT(*) FROM crm.FxRates WHERE CurrencyCode = 'NOK'");
        Assert.Equal(5, realBefore);
        var totalBefore = await db.CountAsync("SELECT COUNT(*) FROM crm.Accounts");

        var output = new StringWriter();
        Assert.Equal(0, await db.SeedAsync(true, output));
        Assert.Contains("Reset: removed 40 accounts, 100 contacts, 180 opportunities, 250 activities and 60 FX rates", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(Expected, await db.DemoCountsAsync());
        Assert.Equal(totalBefore, await db.CountAsync("SELECT COUNT(*) FROM crm.Accounts"));
        Assert.True(await db.ScalarAsync<int>("SELECT MIN(AccountId) FROM crm.Accounts WHERE Name <> N'Real Customer A/S'") > firstAccountId, "reseeded rows are new rows");
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Accounts WHERE Name = N'Real Customer A/S'"));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Contacts WHERE LastName = N'Real'"));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Opportunities WHERE Name = N'Real deal'"));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Activities WHERE Subject = N'Real note'"));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.FxRates WHERE CurrencyCode = 'NOK'"));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey = 'demo.seeded'"));
        // Test users and settings from seed-test and migrations are untouched.
        Assert.Equal(7, await db.CountAsync("SELECT COUNT(*) FROM crm.Users WHERE LOWER(CONVERT(char(36), EntraOid)) NOT LIKE '00000000-0000-4000-8000-00000000d0%'"));
        Assert.Equal(9, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey <> 'demo.seeded'"));
    }

    [SqlFact]
    public async Task Reset_keeps_a_demo_account_that_a_real_row_still_references()
    {
        await using var db = await DemoDb.CreateAsync();
        Assert.Equal(0, await db.SeedAsync(false));
        await db.ScalarAsync<int>("""
            DECLARE @u int = (SELECT UserId FROM crm.Users WHERE EntraOid = '00000000-0000-4000-8000-00000000a002'), @t int = (SELECT TeamId FROM crm.Users WHERE EntraOid = '00000000-0000-4000-8000-00000000a002');
            INSERT crm.Activities (ActivityType, Subject, AccountId, OwnerId, TeamId, CreatedBy, ModifiedBy)
              SELECT TOP 1 'note', N'Real note on a demo account', AccountId, @u, @t, @u, @u FROM crm.Accounts ORDER BY AccountId;
            SELECT 1;
            """);
        var output = new StringWriter();
        Assert.Equal(0, await db.SeedAsync(true, output));
        Assert.Contains("kept because rows created by real users still reference them", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Activities WHERE Subject = N'Real note on a demo account'"));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Activities a JOIN crm.Accounts x ON x.AccountId = a.AccountId WHERE a.Subject = N'Real note on a demo account'"));
    }

    [SqlFact]
    public async Task Rows_without_the_marker_need_an_explicit_reset()
    {
        await using var db = await DemoDb.CreateAsync();
        Assert.Equal(0, await db.SeedAsync(false));
        await db.ScalarAsync<int>("DELETE FROM crm.Settings WHERE SettingKey = 'demo.seeded'; SELECT 1;");
        var err = new StringWriter();
        var m = new Migrator(new SqlConnectionFactory(db.OwnerDb), TextWriter.Null, err);
        Assert.Equal(1, await m.SeedDemoAsync(false, TimeProvider.System, CancellationToken.None));
        Assert.Contains("--reset-demo", err.ToString(), StringComparison.Ordinal);
        Assert.Equal(Expected, await db.DemoCountsAsync());
        Assert.Equal(0, await m.SeedDemoAsync(true, TimeProvider.System, CancellationToken.None));
        Assert.Equal(Expected, await db.DemoCountsAsync());
    }

    [SqlFact]
    public async Task The_sql_checks_including_non_zero_dashboard_kpis_and_rls_integration_pass_on_demo_data()
    {
        await using var db = await DemoDb.CreateAsync();
        Assert.Equal(0, await db.SeedAsync(false));
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.True(0 == await db.Migrator(o).RunSqlFileAsync("db/tests/demo-seed-check.sql", CancellationToken.None), o + " " + e);
        Assert.Equal(0, await new Migrator(new SqlConnectionFactory(db.OwnerDb), TextWriter.Null, e).RunSqlFileAsync("db/tests/rls-integration.sql", CancellationToken.None));
    }

    [SqlFact]
    public async Task The_edge_fx_variant_seeds_and_resets_back_to_the_clean_one()
    {
        await using var db = await DemoDb.CreateAsync();
        Assert.Equal(0, await db.Migrator(TextWriter.Null).SeedDemoAsync(false, TimeProvider.System, CancellationToken.None, fxEdge: true));
        Assert.Equal(2, await db.CountAsync("SELECT COUNT(*) FROM crm.Opportunities WHERE CurrencyCode = 'NOK'"));
        Assert.Equal(59, await db.CountAsync("SELECT COUNT(*) FROM crm.FxRates"));
        Assert.Equal(0, await db.SeedAsync(true));
        Assert.Equal(0, await db.CountAsync("SELECT COUNT(*) FROM crm.Opportunities WHERE CurrencyCode = 'NOK'"));
        Assert.Equal(60, await db.CountAsync("SELECT COUNT(*) FROM crm.FxRates"));
    }
}
