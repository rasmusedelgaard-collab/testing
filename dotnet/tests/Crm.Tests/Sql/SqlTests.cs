using Crm.Web.Auth;
using Crm.Web.Config;
using Crm.Web.Data;
using Crm.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Crm.Tests.Sql;

[Collection("sql")]
public sealed class MigratorTests(SqlServerFixture fx)
{
    private static readonly string[] Golden =
    [
        "21f1d90c4325a4695b50b7091033a6ca6477750694ef743af694d4cbdd7c6963",
        "3856a7f0b33961a8049b46c1e820e77fa73114f6bda922670f8a151cfe7765f4",
        "c1bc06c7eba46ba449aa5b316e762aa7f5ac144a9886550d410a1cbd7068a0a0",
        "3039dca397f42ac8305c2ef1957bce7f484f40b4aa351003dd0e6c4f24d5e8bf",
    ];

    private static async Task<string> NewEmptyDbAsync(SqlServerFixture fx)
    {
        var name = "crm_mig_" + Guid.NewGuid().ToString("N");
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(SqlEnv.SaBuilder().ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await master.OpenAsync();
        await SqlServerFixture.ExecAsync(master, $"CREATE DATABASE [{name}]");
        _ = fx;
        return name;
    }

    private static async Task DropAsync(string name)
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(SqlEnv.SaBuilder().ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await master.OpenAsync();
        await SqlServerFixture.ExecAsync(master, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]");
    }

    [SqlFact]
    public async Task Applies_001_to_004_records_golden_checksums_and_a_rerun_is_a_noop()
    {
        var db = await NewEmptyDbAsync(fx);
        try
        {
            var cfg = SqlEnv.ToDbConfig(SqlEnv.SaBuilder(), db);
            var first = new StringWriter();
            Assert.Equal(0, await new Migrator(new SqlConnectionFactory(cfg), first, Console.Error).MigrateAsync(CancellationToken.None));
            Assert.Contains("apply  001_schema.sql", first.ToString(), StringComparison.Ordinal);
            Assert.Contains("Security policies CrmRowPolicy and CrmConfigPolicy are enabled.", first.ToString(), StringComparison.Ordinal);
            var second = new StringWriter();
            Assert.Equal(0, await new Migrator(new SqlConnectionFactory(cfg), second, Console.Error).MigrateAsync(CancellationToken.None));
            Assert.DoesNotContain("apply ", second.ToString(), StringComparison.Ordinal);
            Assert.Equal(4, second.ToString().Split('\n').Count(l => l.StartsWith("skip ", StringComparison.Ordinal)));

            await using var c = new SqlConnection(new SqlConnectionStringBuilder(SqlEnv.SaBuilder().ConnectionString) { InitialCatalog = db }.ConnectionString);
            await c.OpenAsync();
            await using var cmd = new SqlCommand("SELECT Checksum FROM dbo.SchemaVersions ORDER BY Version", c);
            await using var r = await cmd.ExecuteReaderAsync();
            var sums = new List<string>();
            while (await r.ReadAsync()) { sums.Add(r.GetString(0).Trim()); }
            Assert.Equal(Golden, sums);
        }
        finally
        {
            await DropAsync(db);
        }
    }

    [SqlFact]
    public async Task Refuses_a_file_changed_after_it_was_applied_and_rolls_back_a_failing_one()
    {
        var db = await NewEmptyDbAsync(fx);
        try
        {
            var cfg = SqlEnv.ToDbConfig(SqlEnv.SaBuilder(), db);
            var factory = new SqlConnectionFactory(cfg);
            Assert.Equal(0, await new Migrator(factory, TextWriter.Null, Console.Error).MigrateAsync(CancellationToken.None));

            var files = Migrator.EmbeddedMigrations().ToList();
            var changed = files.Select(f => f.File == "002_rls.sql" ? (f.File, System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(f.Bytes) + "\n-- edited")) : f).ToList();
            await using var conn = await factory.OpenMigratorAsync(CancellationToken.None);
            var m = new Migrator(factory, TextWriter.Null, TextWriter.Null);
            var e = await Assert.ThrowsAsync<MigrationException>(() => m.MigrateAsync(conn, changed, CancellationToken.None));
            Assert.Contains("002_rls.sql was changed after it was applied", e.Message, StringComparison.Ordinal);

            var bad = files.Append(("005_bad.sql", System.Text.Encoding.UTF8.GetBytes("CREATE TABLE dbo.Marker (a int);\nGO\nSELECT 1/0;"))).ToList();
            var e2 = await Assert.ThrowsAsync<MigrationException>(() => m.MigrateAsync(conn, bad, CancellationToken.None));
            Assert.Contains("005_bad.sql failed and was rolled back", e2.Message, StringComparison.Ordinal);
            await using var cmd = new SqlCommand("SELECT (SELECT COUNT(*) FROM sys.tables WHERE name = 'Marker'), (SELECT COUNT(*) FROM dbo.SchemaVersions)", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal(0, r.GetInt32(0));
            Assert.Equal(4, r.GetInt32(1));
        }
        finally
        {
            await DropAsync(db);
        }
    }

    [SqlFact]
    public async Task Seed_is_idempotent_and_updates_the_named_admin_instead_of_duplicating()
    {
        var m = new Migrator(new SqlConnectionFactory(fx.OwnerDb), TextWriter.Null, Console.Error);
        var admin = CrmConfigLoader.LoadTestAdmin("Seed Idempotency Admin", "seed.idempotent@example.com");
        Assert.Equal(0, await m.SeedTestAsync(admin, CancellationToken.None));
        Assert.Equal(0, await m.SeedTestAsync(admin, CancellationToken.None));
        await using var c = new SqlConnection(SqlConnectionFactory.Build(fx.OwnerDb, "t", 1, false, 30));
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*), MAX(DisplayName) FROM crm.Users WHERE LOWER(CONVERT(char(36), EntraOid)) = @oid", c);
        cmd.Parameters.AddWithValue("@oid", admin!.Oid);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(1, r.GetInt32(0));
        Assert.Equal("Seed Idempotency Admin", r.GetString(1));
    }

    [SqlFact]
    public async Task Run_sql_reports_fail_rows_and_batch_errors_and_continues()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "SELECT 'C1' AS [Case], 'PASS' AS Result, 'ok' AS Description;\nGO\nSELECT 1/0;\nGO\nSELECT 'C2' AS [Case], 'FAIL' AS Result, 'no' AS Description, 'why' AS Detail;\n");
            var o = new StringWriter();
            var e = new StringWriter();
            Assert.Equal(1, await new Migrator(new SqlConnectionFactory(fx.OwnerDb), o, e).RunSqlFileAsync(file, CancellationToken.None));
            Assert.Contains("C1       PASS", o.ToString(), StringComparison.Ordinal);
            Assert.Contains("C2       FAIL  no | why", o.ToString(), StringComparison.Ordinal);
            Assert.Contains("Batch 2/3 FAILED", e.ToString(), StringComparison.Ordinal);
            Assert.Contains("SQL error 8134", e.ToString(), StringComparison.Ordinal);
            Assert.Contains("2 case(s) FAILED", e.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

[Collection("sql")]
public sealed class RlsDbTests(SqlServerFixture fx)
{
    private RlsDb NewDb(DbConfig? cfg = null) => new(new SqlConnectionFactory(cfg ?? fx.AppDb), new DbRetry(TimeProvider.System, NullLogger<DbRetry>.Instance), null!, NullLogger<RlsDb>.Instance);

    [SqlFact]
    public async Task Sets_the_session_context_for_the_transaction()
    {
        var seen = await NewDb().RunAsync(new RlsContext(42, CrmRole.Manager, 7), async tx =>
            await tx.QuerySingleOrDefaultAsync<(int, string, int)>("SELECT CONVERT(int, SESSION_CONTEXT(N'UserId')) AS A, CONVERT(nvarchar(10), SESSION_CONTEXT(N'Role')) AS B, CONVERT(int, SESSION_CONTEXT(N'TeamId')) AS C"), CancellationToken.None);
        Assert.Equal((42, "Manager", 7), seen);
    }

    [SqlFact]
    public async Task Null_team_stays_null()
    {
        var team = await NewDb().RunAsync(new RlsContext(1, CrmRole.User, null), tx => tx.ExecuteScalarAsync<int?>("SELECT CONVERT(int, SESSION_CONTEXT(N'TeamId'))"), CancellationToken.None);
        Assert.Null(team);
    }

    [SqlFact]
    public async Task A_second_call_in_the_same_request_is_refused()
    {
        var db = NewDb();
        await db.RunAsync(new RlsContext(1, CrmRole.User, null), tx => tx.ExecuteScalarAsync<int>("SELECT 1"), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.RunAsync(new RlsContext(1, CrmRole.User, null), tx => tx.ExecuteScalarAsync<int>("SELECT 1"), CancellationToken.None));
    }

    [SqlFact]
    public async Task An_invalid_context_throws_before_any_database_call()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NewDb().RunAsync(new RlsContext(0, CrmRole.User, null), tx => tx.ExecuteScalarAsync<int>("SELECT 1"), CancellationToken.None));
    }

    [SqlFact]
    public async Task Stale_context_on_the_connection_trips_the_guard_with_51000()
    {
        await using var conn = new SqlConnection(SqlConnectionFactory.Build(fx.AppDb, "guard-test", 1, false, 30));
        await conn.OpenAsync();
        await SqlServerFixture.ExecAsync(conn, "EXEC sp_set_session_context @key = N'UserId', @value = 99, @read_only = 1");
        await using var cmd = new SqlCommand(RlsDb.ContextBatch, conn);
        cmd.Parameters.AddWithValue("@UserId", 1);
        cmd.Parameters.AddWithValue("@Role", "User");
        cmd.Parameters.AddWithValue("@TeamId", DBNull.Value);
        var e = await Assert.ThrowsAsync<SqlException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(51000, e.Number);
    }

    [SqlFact]
    public async Task Commands_are_serialised_even_with_Task_WhenAll()
    {
        var sums = await NewDb().RunAsync(new RlsContext(1, CrmRole.User, null), async tx =>
        {
            var tasks = Enumerable.Range(1, 8).Select(i => tx.ExecuteScalarAsync<int>($"SELECT {i}")).ToArray();
            return await Task.WhenAll(tasks);
        }, CancellationToken.None);
        Assert.Equal(Enumerable.Range(1, 8), sums);
    }

    [SqlFact]
    public async Task Rls_block_maps_to_forbidden_and_the_failed_write_is_rolled_back()
    {
        var team = "T-" + Guid.NewGuid().ToString("N")[..8];
        await Assert.ThrowsAsync<ForbiddenException>(() => NewDb().RunAsync(new RlsContext(1, CrmRole.User, null), tx =>
            tx.ExecuteAsync("INSERT INTO crm.Teams (Name, EntraGroupId) VALUES (@n, NEWID())", new { n = team }), CancellationToken.None));
        var count = await NewDb().RunAsync(new RlsContext(1, CrmRole.Admin, null), tx => tx.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM crm.Teams WHERE Name = @n", new { n = team }), CancellationToken.None);
        Assert.Equal(0, count);
    }

    [SqlFact]
    public async Task Admin_can_write_teams_and_an_exception_inside_the_callback_rolls_back()
    {
        var team = "T-" + Guid.NewGuid().ToString("N")[..8];
        await Assert.ThrowsAsync<InvalidOperationException>(() => NewDb().RunAsync<int>(new RlsContext(1, CrmRole.Admin, null), async tx =>
        {
            await tx.ExecuteAsync("INSERT INTO crm.Teams (Name, EntraGroupId) VALUES (@n, NEWID())", new { n = team });
            throw new InvalidOperationException("boom");
        }, CancellationToken.None));
        Assert.Equal(0, await NewDb().RunAsync(new RlsContext(1, CrmRole.Admin, null), tx => tx.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM crm.Teams WHERE Name = @n", new { n = team }), CancellationToken.None));
        await NewDb().RunAsync(new RlsContext(1, CrmRole.Admin, null), tx => tx.ExecuteAsync("INSERT INTO crm.Teams (Name, EntraGroupId) VALUES (@n, NEWID())", new { n = team }), CancellationToken.None);
        Assert.Equal(1, await NewDb().RunAsync(new RlsContext(1, CrmRole.Admin, null), tx => tx.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM crm.Teams WHERE Name = @n", new { n = team }), CancellationToken.None));
    }
}

[Collection("sql")]
public sealed class PoolResetTests(SqlServerFixture fx)
{
    [SqlFact]
    public async Task Second_user_on_a_one_connection_pool_gets_their_own_context_and_no_51000()
    {
        var cfg = fx.AppDb with { PoolMax = 1 };
        var factory = new SqlConnectionFactory(cfg);
        RlsDb Db() => new(factory, new DbRetry(TimeProvider.System, NullLogger<DbRetry>.Instance), null!, NullLogger<RlsDb>.Instance);
        var (spidA, uidA) = await Db().RunAsync(new RlsContext(11, CrmRole.User, null), async tx => (await tx.ExecuteScalarAsync<int>("SELECT @@SPID"), await tx.ExecuteScalarAsync<int>("SELECT CONVERT(int, SESSION_CONTEXT(N'UserId'))")), CancellationToken.None);
        var (spidB, uidB) = await Db().RunAsync(new RlsContext(22, CrmRole.Manager, 3), async tx => (await tx.ExecuteScalarAsync<int>("SELECT @@SPID"), await tx.ExecuteScalarAsync<int>("SELECT CONVERT(int, SESSION_CONTEXT(N'UserId'))")), CancellationToken.None);
        Assert.Equal(11, uidA);
        Assert.Equal(22, uidB);
        Assert.Equal(spidA, spidB); // the same physical connection was reused
    }

    [SqlFact]
    public async Task A_read_only_key_does_not_survive_the_pool()
    {
        var cs = SqlConnectionFactory.Build(fx.AppDb, "pool-reset-" + Guid.NewGuid().ToString("N"), 1, true, 30);
        int spid1;
        await using (var c1 = new SqlConnection(cs))
        {
            await c1.OpenAsync();
            await SqlServerFixture.ExecAsync(c1, "EXEC sp_set_session_context @key = N'UserId', @value = 5, @read_only = 1");
            await using var cmd = new SqlCommand("SELECT @@SPID", c1);
            spid1 = Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
        await using var c2 = new SqlConnection(cs);
        await c2.OpenAsync();
        await using var cmd2 = new SqlCommand("SELECT @@SPID, SESSION_CONTEXT(N'UserId')", c2);
        await using var r = await cmd2.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(spid1, Convert.ToInt32(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(r.IsDBNull(1));
    }
}

[Collection("sql")]
public sealed class TicketStoreAndUsersTests(SqlServerFixture fx)
{
    private (SystemDb Db, FakeTimeProvider Time, SqlTicketStore Store) Make(int idle = 60)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var db = new SystemDb(new SqlConnectionFactory(fx.AppDb), new DbRetry(time, NullLogger<DbRetry>.Instance));
        return (db, time, new SqlTicketStore(db, time, new SessionTimeouts(idle, 10), NullLogger<SqlTicketStore>.Instance));
    }

    private static AuthenticationTicket Ticket(long authAt) =>
        new(CrmPrincipal.Create(new SessionUser(3, "00000000-0000-4000-8000-00000000a003", "Test User", "u@test.invalid", CrmRole.User, 2, authAt)), new AuthenticationProperties(), "Cookies");

    [SqlFact]
    public async Task Store_retrieve_renew_remove_round_trip_without_tokens()
    {
        var (db, time, store) = Make();
        var key = await store.StoreAsync(Ticket(time.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.Equal(43, key.Length);
        var data = (await db.QueryAsync<string>("SELECT Data FROM app.Sessions WHERE Sid = @sid", new { sid = key }, CancellationToken.None)).Single();
        Assert.Contains("\"V\":1", data, StringComparison.Ordinal);
        Assert.DoesNotContain("token", data, StringComparison.OrdinalIgnoreCase);
        var t = await store.RetrieveAsync(key);
        Assert.Equal(CrmRole.User, CrmPrincipal.ToSessionUser(t!.Principal)!.Role);
        t.Properties.Items[CrmClaims.ActiveCheckedAtItem] = "123";
        await store.RenewAsync(key, t);
        Assert.Equal("123", (await store.RetrieveAsync(key))!.Properties.Items[CrmClaims.ActiveCheckedAtItem]);
        await store.RemoveAsync(key);
        Assert.Null(await store.RetrieveAsync(key));
    }

    [SqlFact]
    public async Task Idle_timeout_is_rolling_and_expired_rows_are_not_returned()
    {
        var (_, time, store) = Make(idle: 10);
        var key = await store.StoreAsync(Ticket(time.GetUtcNow().ToUnixTimeMilliseconds()));
        time.Advance(TimeSpan.FromMinutes(8));
        Assert.NotNull(await store.RetrieveAsync(key)); // touches: now valid until +18
        time.Advance(TimeSpan.FromMinutes(8));
        Assert.NotNull(await store.RetrieveAsync(key));
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(await store.RetrieveAsync(key));
    }

    [SqlFact]
    public async Task Unknown_or_oversized_keys_return_nothing()
    {
        var (_, _, store) = Make();
        Assert.Null(await store.RetrieveAsync("nope"));
        Assert.Null(await store.RetrieveAsync(new string('a', 200)));
        Assert.Null(await store.RetrieveAsync("'; DROP TABLE app.Sessions;--"));
    }

    [SqlFact]
    public async Task Login_upsert_creates_a_user_and_is_idempotent_and_overage_gives_no_team()
    {
        var (db, _, _) = Make();
        var repo = new UserRepo(db);
        var oid = Guid.NewGuid().ToString();
        var a = await repo.UpsertOnLoginAsync(oid, Guid.NewGuid().ToString(), "New Person", "np@example.com", CrmRole.User, new GroupsInfo([], false), CancellationToken.None);
        var b = await repo.UpsertOnLoginAsync(oid, Guid.NewGuid().ToString(), "New Person", "np@example.com", CrmRole.Manager, new GroupsInfo([], true), CancellationToken.None);
        Assert.Equal(a.UserId, b.UserId);
        Assert.Equal(TeamStatus.None, a.TeamStatus);
        Assert.Equal(TeamStatus.Overage, b.TeamStatus);
        Assert.Null(b.TeamId);
        Assert.True(await repo.IsActiveAsync(a.UserId, CancellationToken.None));
        Assert.False(await repo.IsActiveAsync(int.MaxValue, CancellationToken.None));
    }

    [SqlFact]
    public async Task A_team_group_gives_the_team_and_a_deactivated_user_is_refused_with_50011()
    {
        var (db, _, _) = Make();
        var repo = new UserRepo(db);
        var oid = Guid.NewGuid().ToString();
        var up = await repo.UpsertOnLoginAsync(oid, Guid.NewGuid().ToString(), "Team Person", null, CrmRole.User, new GroupsInfo(["00000000-0000-4000-8000-0000000fa001"], false), CancellationToken.None);
        Assert.Equal(TeamStatus.Ok, up.TeamStatus);
        Assert.NotNull(up.TeamId);
        await using (var owner = new SqlConnection(SqlConnectionFactory.Build(fx.OwnerDb, "owner", 1, false, 30)))
        {
            await owner.OpenAsync();
            await SqlServerFixture.ExecAsync(owner, $"UPDATE crm.Users SET IsActive = 0 WHERE UserId = {up.UserId}");
        }
        var e = await Assert.ThrowsAnyAsync<Exception>(() => repo.UpsertOnLoginAsync(oid, Guid.NewGuid().ToString(), "Team Person", null, CrmRole.User, new GroupsInfo([], false), CancellationToken.None));
        Assert.True(Transient.HasNumber(e, UserRepo.UserDeactivated));
        Assert.False(await repo.IsActiveAsync(up.UserId, CancellationToken.None));
    }

    [SqlFact]
    public async Task Test_user_picker_offers_seeds_and_the_named_admin_but_never_a_real_account()
    {
        var (db, _, _) = Make();
        var repo = new TestUserRepo(db);
        var list = await repo.ListAsync(CancellationToken.None);
        Assert.True(list.Count(u => u.Oid.StartsWith("00000000-0000-4000-8000-", StringComparison.Ordinal)) >= 8);
        Assert.Contains(list, u => u.Name == "Fixture Admin");
        var real = Guid.NewGuid().ToString();
        await new UserRepo(db).UpsertOnLoginAsync(real, Guid.NewGuid().ToString(), "Real Employee", null, CrmRole.Admin, new GroupsInfo([], false), CancellationToken.None);
        Assert.DoesNotContain(await repo.ListAsync(CancellationToken.None), u => u.Name == "Real Employee");
        Assert.Null(await repo.FindAsync(real, CancellationToken.None));
        Assert.Null(await repo.FindAsync("00000000-0000-4000-8000-ffffffffffff", CancellationToken.None));
        Assert.Null(await repo.FindAsync("not-a-guid", CancellationToken.None));
        Assert.NotNull(await repo.FindAsync(TestUsersFirst, CancellationToken.None));
    }

    private static string TestUsersFirst => Crm.Web.Services.TestUsers.SeedOids[0];

    [SqlFact]
    public async Task Branding_cache_reads_defaults_from_the_seeded_database()
    {
        var (db, time, _) = Make();
        var cache = new BrandingCache(db, time);
        var b = await cache.GetAsync(CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(b.AppName));
        Assert.Matches("^#[0-9A-Fa-f]{6}$", b.AccentColor);
    }
}
