using System.Globalization;
using Crm.Web.Config;
using Crm.Web.Data;
using Crm.Web.Services.Demo;
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

    public async Task<int> SeedAsync(bool reset, TextWriter? output = null, bool demoSettings = false, bool fxEdge = false, bool purgeLegacy = false) =>
        await Migrator(output ?? TextWriter.Null).SeedDemoAsync(reset, TimeProvider.System, CancellationToken.None, fxEdge, demoSettings, purgeLegacy);

    /// <summary>Records the current (not yet reset) run inserted, found the way the seeder finds them: 'C' audit rows with the stored RunId.</summary>
    public Task<int> TrackedAsync(string entityType, string table, string idColumn) => CountAsync(
        $"SELECT COUNT(*) FROM crm.{table} WHERE {idColumn} IN (SELECT EntityId FROM crm.AuditLog WHERE Action = 'C' AND EntityType = '{entityType}' " +
        "AND CorrelationId = (SELECT TRY_CAST(SettingValue AS uniqueidentifier) FROM crm.Settings WHERE SettingKey = 'demo.runId'))");

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
    // The counts come from the generator itself (they do not depend on the date), so changing the dataset needs no edit here.
    private static readonly DemoDataset Data = DemoGenerator.Generate(DateOnly.FromDateTime(DateTime.UtcNow));
    private static readonly int[] Expected = [Data.Accounts.Count, Data.Contacts.Count, Data.Opportunities.Count, Data.Activities.Count, Data.FxRates.Count];

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
        Assert.Equal(0, await db.CountAsync("SELECT COUNT(*) FROM crm.AuditLog a JOIN crm.Users u ON u.UserId = a.ChangedBy WHERE u.Role = 'Admin' AND a.EntityType <> 'fxrate'"));
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
        Assert.Contains($"removed {Expected[0]} accounts, {Expected[1]} contacts, {Expected[2]} opportunities, {Expected[3]} activities and {Expected[4]} FX rates", output.ToString(), StringComparison.Ordinal);
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
        Assert.Equal(10, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey NOT IN ('demo.seeded', 'demo.runId')")); // 9 defaults + the test marker
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey = 'demo.runId'"));
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
        Assert.Equal(Expected[4] - 1, await db.CountAsync("SELECT COUNT(*) FROM crm.FxRates"));
        Assert.Equal(0, await db.SeedAsync(true));
        Assert.Equal(0, await db.CountAsync("SELECT COUNT(*) FROM crm.Opportunities WHERE CurrencyCode = 'NOK'"));
        Assert.Equal(Expected[4], await db.CountAsync("SELECT COUNT(*) FROM crm.FxRates"));
    }

    private const string Lars = "(SELECT UserId FROM crm.Users WHERE EntraOid = '00000000-0000-4000-8000-00000000d002')";
    private const string LarsTeam = "(SELECT TeamId FROM crm.Users WHERE EntraOid = '00000000-0000-4000-8000-00000000d002')";

    [SqlFact]
    public async Task Seed_test_writes_the_test_marker_once()
    {
        await using var db = await DemoDb.CreateAsync();
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey = 'environment' AND SettingValue = N'test'"));
        Assert.Equal(0, await db.Migrator(TextWriter.Null).SeedTestAsync(null, CancellationToken.None));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey = 'environment'"));
    }

    [SqlFact]
    public async Task Seed_demo_refuses_a_database_without_the_test_marker_and_writes_nothing()
    {
        await using var db = await DemoDb.CreateAsync();
        await db.ScalarAsync<int>("DELETE FROM crm.Settings WHERE SettingKey = 'environment'; SELECT 1;");
        var err = new StringWriter();
        var m = new Migrator(new SqlConnectionFactory(db.OwnerDb), TextWriter.Null, err);
        Assert.Equal(1, await m.SeedDemoAsync(false, TimeProvider.System, CancellationToken.None));
        Assert.Contains("no test marker", err.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await m.SeedDemoAsync(true, TimeProvider.System, CancellationToken.None, demoSettings: true));
        Assert.Equal(new int[Expected.Length], await db.DemoCountsAsync());
        Assert.Equal(7, await db.CountAsync("SELECT COUNT(*) FROM crm.Users")); // not even the demo users and teams were created
        Assert.Equal(0, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey IN ('demo.seeded', 'demo.runId')"));
        // A marker with any other value does not count either.
        await db.ScalarAsync<int>("INSERT crm.Settings (SettingKey, SettingValue) VALUES ('environment', N'production'); SELECT 1;");
        Assert.Equal(1, await m.SeedDemoAsync(false, TimeProvider.System, CancellationToken.None));
        Assert.Equal(new int[Expected.Length], await db.DemoCountsAsync());
    }

    [SqlFact]
    public async Task Reset_keeps_everything_a_person_created_while_signed_in_as_a_demo_user()
    {
        await using var db = await DemoDb.CreateAsync();
        // A real rate for a month the seeder also covers: the seeder must neither overwrite nor, later, delete it.
        await db.ScalarAsync<int>("""
            DECLARE @a int = (SELECT UserId FROM crm.Users WHERE EntraOid = '00000000-0000-4000-8000-00000000a001');
            INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) VALUES ('DKK', 'EUR', DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1), 7.0, @a);
            SELECT 1;
            """);
        Assert.Equal(0, await db.SeedAsync(false));
        Assert.Equal(Expected[4] - 1, await db.CountAsync("SELECT COUNT(*) FROM crm.FxRates WHERE ModifiedBy IN (SELECT UserId FROM crm.Users WHERE LOWER(CONVERT(char(36), EntraOid)) LIKE '00000000-0000-4000-8000-00000000d0%')"));

        // Lars Eriksen (a demo user) works in the app: a new account with a contact, a deal and a note, a note on a demo deal, and a new FX rate by the demo admin.
        await db.ScalarAsync<int>($$"""
            DECLARE @u int = {{Lars}}, @t int = {{LarsTeam}};
            DECLARE @admin int = (SELECT UserId FROM crm.Users WHERE EntraOid = '00000000-0000-4000-8000-00000000d001');
            INSERT crm.Accounts (Name, OwnerId, TeamId, CreatedBy, ModifiedBy) VALUES (N'Typed by a person A/S', @u, @t, @u, @u);
            DECLARE @acc int = SCOPE_IDENTITY();
            INSERT crm.Contacts (AccountId, LastName, OwnerId, TeamId, CreatedBy, ModifiedBy) VALUES (@acc, N'Typed', @u, @t, @u, @u);
            INSERT crm.Opportunities (Name, AccountId, StageId, Amount, CurrencyCode, CloseDate, OwnerId, TeamId, CreatedBy, ModifiedBy)
              VALUES (N'Typed deal', @acc, (SELECT TOP 1 StageId FROM crm.PipelineStages WHERE StageType = 'open' ORDER BY SortOrder), 1000, 'DKK', '2030-01-01', @u, @t, @u, @u);
            INSERT crm.Activities (ActivityType, Subject, AccountId, OwnerId, TeamId, CreatedBy, ModifiedBy) VALUES ('note', N'Typed note', @acc, @u, @t, @u, @u);
            INSERT crm.Activities (ActivityType, Subject, AccountId, OpportunityId, OwnerId, TeamId, CreatedBy, ModifiedBy)
              SELECT TOP 1 'note', N'Typed note on a demo deal', o.AccountId, o.OpportunityId, @u, @t, @u, @u FROM crm.Opportunities o WHERE o.OwnerId = @u AND o.PrimaryContactId IS NOT NULL ORDER BY o.OpportunityId;
            INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) VALUES ('DKK', 'NOK', '2030-01-01', 0.65, @admin);
            SELECT 1;
            """);
        var typed = new[]
        {
            "SELECT COUNT(*) FROM crm.Accounts WHERE Name = N'Typed by a person A/S'",
            "SELECT COUNT(*) FROM crm.Contacts WHERE LastName = N'Typed'",
            "SELECT COUNT(*) FROM crm.Opportunities WHERE Name = N'Typed deal'",
            "SELECT COUNT(*) FROM crm.Activities WHERE Subject IN (N'Typed note', N'Typed note on a demo deal')",
            "SELECT COUNT(*) FROM crm.FxRates WHERE CurrencyCode = 'NOK'",
        };
        Assert.Equal(new[] { 1, 1, 1, 2, 1 }, await Task.WhenAll(typed.Select(db.CountAsync)));
        var oldRun = await db.ScalarAsync<string>("SELECT SettingValue FROM crm.Settings WHERE SettingKey = 'demo.runId'");

        var output = new StringWriter();
        Assert.Equal(0, await db.SeedAsync(true, output));
        var log = output.ToString();
        // Only what the seeder inserted is removed: the kept deal (it has a person's note) keeps its account and contact.
        Assert.Contains($"removed {Expected[0] - 1} accounts, {Expected[1] - 1} contacts, {Expected[2] - 1} opportunities, {Expected[3]} activities and {Expected[4] - 1} FX rates", log, StringComparison.Ordinal);
        Assert.Contains("were kept", log, StringComparison.Ordinal);
        Assert.Equal(new[] { 1, 1, 1, 2, 1 }, await Task.WhenAll(typed.Select(db.CountAsync)));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.FxRates WHERE CurrencyCode = 'EUR' AND Rate = 7.0")); // the real rate was kept
        Assert.NotEqual(oldRun, await db.ScalarAsync<string>("SELECT SettingValue FROM crm.Settings WHERE SettingKey = 'demo.runId'"));
        // The fresh run is complete and is the only one tracked.
        Assert.Equal(Expected[0], await db.TrackedAsync("account", "Accounts", "AccountId"));
        Assert.Equal(Expected[1], await db.TrackedAsync("contact", "Contacts", "ContactId"));
        Assert.Equal(Expected[2], await db.TrackedAsync("opportunity", "Opportunities", "OpportunityId"));
        Assert.Equal(Expected[3], await db.TrackedAsync("activity", "Activities", "ActivityId"));
        Assert.Equal(Expected[4] - 1, await db.TrackedAsync("fxrate", "FxRates", "FxRateId"));
        // A second reset with nothing typed in between removes the fresh run completely again, and the person's rows still survive.
        Assert.Equal(0, await db.SeedAsync(true));
        Assert.Equal(new[] { 1, 1, 1, 2, 1 }, await Task.WhenAll(typed.Select(db.CountAsync)));
    }

    [SqlFact]
    public async Task Reset_refuses_demo_data_from_a_version_that_did_not_record_its_rows()
    {
        await using var db = await DemoDb.CreateAsync();
        Assert.Equal(0, await db.SeedAsync(false));
        await db.ScalarAsync<int>("DELETE FROM crm.Settings WHERE SettingKey = 'demo.runId'; SELECT 1;");
        var err = new StringWriter();
        var m = new Migrator(new SqlConnectionFactory(db.OwnerDb), TextWriter.Null, err);
        Assert.Equal(1, await m.SeedDemoAsync(true, TimeProvider.System, CancellationToken.None));
        Assert.Contains("did not record its rows", err.ToString(), StringComparison.Ordinal);
        Assert.Equal(Expected, await db.DemoCountsAsync()); // nothing was deleted or added
    }

    [SqlFact]
    public async Task Demo_settings_set_team_visibility_danish_locale_and_the_demo_currencies_only_when_asked()
    {
        await using var plain = await DemoDb.CreateAsync();
        Assert.Equal(0, await plain.SeedAsync(false));
        Assert.Equal(1, await plain.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey = 'visibility.mode' AND SettingValue = N'private'"));

        await using var db = await DemoDb.CreateAsync();
        await db.ScalarAsync<int>("UPDATE crm.Settings SET SettingValue = N'en-GB' WHERE SettingKey = 'locale.code'; SELECT 1;");
        Assert.Equal(0, await db.SeedAsync(false, demoSettings: true));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey = 'visibility.mode' AND SettingValue = N'team'"));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey = 'locale.code' AND SettingValue = N'da-DK'"));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey = 'currency.enabled' AND SettingValue = N'DKK,EUR,SEK,USD'"));
        // The generic industry values stay active: that is a product-owner decision.
        Assert.Equal(0, await db.CountAsync("SELECT COUNT(*) FROM crm.PicklistValues v JOIN crm.Picklists p ON p.PicklistId = v.PicklistId WHERE p.Code = 'industry' AND v.IsActive = 0"));
        // Asking again on a seeded database applies the settings without touching the data.
        await db.ScalarAsync<int>("UPDATE crm.Settings SET SettingValue = N'private' WHERE SettingKey = 'visibility.mode'; SELECT 1;");
        Assert.Equal(0, await db.SeedAsync(false, demoSettings: true));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey = 'visibility.mode' AND SettingValue = N'team'"));
        Assert.Equal(Expected, await db.DemoCountsAsync());
        // NOK is enabled only for the edge variant.
        Assert.Equal(0, await db.SeedAsync(true, demoSettings: true, fxEdge: true));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey = 'currency.enabled' AND SettingValue = N'DKK,EUR,SEK,USD,NOK'"));
    }

    [SqlFact]
    public async Task Purge_legacy_demo_removes_old_style_data_then_a_new_seed_records_a_run_id_and_reset_works()
    {
        await using var db = await DemoDb.CreateAsync();
        // Not legacy yet: nothing to purge, nothing changes.
        var err = new StringWriter();
        var m = new Migrator(new SqlConnectionFactory(db.OwnerDb), TextWriter.Null, err);
        Assert.Equal(1, await m.SeedDemoAsync(false, TimeProvider.System, CancellationToken.None, purgeLegacy: true));
        Assert.Contains("applies only to demo data without a run id", err.ToString(), StringComparison.Ordinal);

        // Legacy state: demo rows and the marker, but no run id (what the first seeder version left behind).
        Assert.Equal(0, await db.SeedAsync(false));
        await db.ScalarAsync<int>("DELETE FROM crm.Settings WHERE SettingKey = 'demo.runId'; SELECT 1;");
        await db.ScalarAsync<int>("""
            DECLARE @u int = (SELECT UserId FROM crm.Users WHERE EntraOid = '00000000-0000-4000-8000-00000000a002'), @t int = (SELECT TeamId FROM crm.Users WHERE EntraOid = '00000000-0000-4000-8000-00000000a002');
            INSERT crm.Accounts (Name, OwnerId, TeamId, CreatedBy, ModifiedBy) VALUES (N'Real customer', @u, @t, @u, @u);
            SELECT 1;
            """);
        var output = new StringWriter();
        Assert.Equal(0, await db.SeedAsync(false, output, purgeLegacy: true));
        var log = output.ToString();
        Assert.Contains($"found {Expected[0]} accounts, {Expected[1]} contacts, {Expected[2]} opportunities, {Expected[3]} activities and {Expected[4]} FX rates", log, StringComparison.Ordinal);
        Assert.Equal(new int[Expected.Length], await db.DemoCountsAsync());
        Assert.Equal(0, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey = 'demo.seeded'"));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Accounts WHERE Name = N'Real customer'"));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.AuditLog WHERE EntityType = 'setting' AND Action = 'D' AND NewValue LIKE N'Legacy demo purge:%'"));
        // Once the marker is gone the purge refuses again.
        Assert.Equal(1, await m.SeedDemoAsync(false, TimeProvider.System, CancellationToken.None, purgeLegacy: true));

        // A normal seed now works, records its run id, and reset removes exactly that run.
        Assert.Equal(0, await db.SeedAsync(false));
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Settings WHERE SettingKey = 'demo.runId'"));
        Assert.Equal(Expected, await db.DemoCountsAsync());
        Assert.Equal(0, await db.SeedAsync(true));
        Assert.Equal(Expected, await db.DemoCountsAsync());
        Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM crm.Accounts WHERE Name = N'Real customer'"));
    }
}
