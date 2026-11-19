using System.Data;
using System.Globalization;
using System.Text;
using Crm.Web.Services;
using Crm.Web.Services.Demo;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Crm.Web.Data;

/// <summary>
/// Writes the generated demo dataset (seed-demo). Runs on the migrator (DB owner) connection but writes through the same tables and
/// row-level-security predicates as the app: each owner's rows are inserted with that owner's session context, so CreatedBy, ModifiedBy and
/// the audit trail name the real owner. One transaction: either the whole set and the marker exist, or nothing changed.
/// Safety: the database must carry the test marker (Settings environment=test, written by seed-test), otherwise nothing is written.
/// Tracking: every record (and FX rate) gets a 'C' audit row carrying this run's CorrelationId (RunId), and the RunId is stored in
/// <see cref="DemoMarker.RunIdKey"/>. A reset deletes exactly the records those audit rows name, never "rows created by a demo user":
/// a person who signed in as a demo user and created data keeps it.
/// </summary>
public sealed class DemoSeeder(TimeProvider time, TextWriter output)
{
    private const int ChunkSize = 40;
    private const string ZeroTenant = "00000000-0000-0000-0000-000000000000";

    private sealed record SettingRow(string SettingKey, string SettingValue);

    private sealed record StageRow(int StageId, string Name, string StageType);

    private sealed record PickRow(string PCode, int ValueId, string Label);

    private sealed record TeamRow(int TeamId, Guid EntraGroupId);

    private sealed record UserRow(int UserId, Guid EntraOid, int? TeamId);

    public async Task<DemoDataset?> RunAsync(SqlConnection conn, bool reset, DemoOptions options, CancellationToken ct)
    {
        // Not committed on an early return or an exception (and never in a dry run): disposing the transaction rolls everything back.
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        // Admin context for the checks and the reset. UserId 0 is a migration-scoped context, not a real user (as in 004_seed.sql).
        await SetContextAsync(conn, tx, 0, "Admin", null, ct).ConfigureAwait(false);
        // One seeder at a time: the lock is held until the transaction ends, and the markers are read only after it is taken,
        // so two concurrent runs cannot both see "no demo data" and both write it.
        await TakeLockAsync(conn, tx, ct).ConfigureAwait(false);
        var settings = (await conn.QueryAsync<SettingRow>(new CommandDefinition(
            "SELECT SettingKey, SettingValue FROM crm.Settings WHERE SettingKey IN (N'currency.base', N'locale.timezone', N'demo.seeded', N'demo.runId', N'environment')", transaction: tx, cancellationToken: ct)).ConfigureAwait(false))
            .ToDictionary(s => s.SettingKey, s => s.SettingValue, StringComparer.Ordinal);
        if (settings.GetValueOrDefault(DemoMarker.TestEnvKey) != DemoMarker.TestEnvValue)
        {
            throw new MigrationException($"seed-demo is refused: this database has no test marker (Settings {DemoMarker.TestEnvKey}={DemoMarker.TestEnvValue}). Run seed-test on the test database first; never run seed-demo against a real database.");
        }
        if (settings.GetValueOrDefault("currency.base", "DKK") != "DKK")
        {
            throw new MigrationException("seed-demo expects the base currency DKK (Settings currency.base); the demo FX rates and amounts are defined against DKK.");
        }
        var today = TimeZones.TodayIn(settings.GetValueOrDefault("locale.timezone"), time);
        var data = DemoGenerator.Generate(today, options);
        var previousRun = Guid.TryParse(settings.GetValueOrDefault(DemoMarker.RunIdKey), out var g) ? g : (Guid?)null;
        var runId = Guid.NewGuid();
        var hasMarker = settings.ContainsKey(DemoMarker.MarkerKey);
        if (options.PurgeLegacy)
        {
            await PurgeLegacyAsync(conn, tx, data, settings, options.DryRun, ct).ConfigureAwait(false);
            if (!options.DryRun) { await tx.CommitAsync(ct).ConfigureAwait(false); }
            return null;
        }
        var rows = previousRun is null ? 0 : await LoadTrackedAsync(conn, tx, previousRun.Value, ct).ConfigureAwait(false);

        if (hasMarker && !reset)
        {
            output.WriteLine($"Demo data is already present ({settings[DemoMarker.MarkerKey]}); nothing to do. Use --reset-demo to rebuild it.");
            if (options.DemoSettings)
            {
                await ApplyDemoSettingsAsync(conn, tx, await AdminIdAsync(conn, tx, data, ct).ConfigureAwait(false), previousRun ?? runId, options, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                output.WriteLine("Applied the demo settings (visibility team, locale da-DK, enabled currencies).");
            }
            return null;
        }
        if (!hasMarker && rows > 0 && !reset)
        {
            throw new MigrationException($"Found {rows} demo rows but no '{DemoMarker.MarkerKey}' marker: the marker was removed. Run with --reset-demo.");
        }
        if (reset)
        {
            if (hasMarker && previousRun is null)
            {
                throw new MigrationException($"Cannot reset: the demo data was written by an earlier version of seed-demo that did not record its rows ('{DemoMarker.RunIdKey}' is missing). Nothing was changed. Remove it by hand, or rebuild the test database.");
            }
            await ResetAsync(conn, tx, previousRun, runId, options.DryRun, output, ct).ConfigureAwait(false);
            if (options.DryRun)
            {
                output.WriteLine("Dry run: nothing was changed.");
                return null;
            }
        }

        await WriteAsync(conn, tx, data, options, runId, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        output.WriteLine($"Seeded demo data relative to {today:yyyy-MM-dd}: {data.Teams.Count} teams, {data.Users.Count} users, {data.Accounts.Count} accounts, {data.Contacts.Count} contacts, "
            + $"{data.Opportunities.Count} opportunities, {data.Activities.Count} activities, {data.FxRates.Count} FX rates.");
        output.WriteLine("Demo users are listed in the test sign-in picker (Astrid Holm is the demo Admin, Lars Eriksen the demo persona).");
        if (options.FxEdge) { output.WriteLine("FX edge variant: SEK for the current month is missing and two open deals are in NOK (no NOK rates)."); }
        if (options.DemoSettings) { output.WriteLine("Applied the demo settings (visibility team, locale da-DK, enabled currencies)."); }
        return data;
    }

    private static async Task TakeLockAsync(SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        var rc = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "DECLARE @rc int; EXEC @rc = sp_getapplock @Resource = N'crm.seed-demo', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000; SELECT @rc;",
            transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
        if (rc < 0)
        {
            throw new MigrationException($"Another seed-demo run holds the lock 'crm.seed-demo' (sp_getapplock returned {rc.ToString(CultureInfo.InvariantCulture)}). Nothing was changed.");
        }
    }

    private static async Task SetContextAsync(SqlConnection c, SqlTransaction tx, int userId, string role, int? teamId, CancellationToken ct) =>
        await c.ExecuteAsync(new CommandDefinition(
            "EXEC sp_set_session_context N'UserId', @u, @read_only = 0; EXEC sp_set_session_context N'Role', @r, @read_only = 0; EXEC sp_set_session_context N'TeamId', @t, @read_only = 0;",
            new { u = userId, r = role, t = teamId }, tx, cancellationToken: ct)).ConfigureAwait(false);

    private static async Task<int> AdminIdAsync(SqlConnection conn, SqlTransaction tx, DemoDataset data, CancellationToken ct)
    {
        var oid = data.Users.First(u => u.Key == DemoMarker.AdminKey).Oid;
        return await conn.QuerySingleAsync<int>(new CommandDefinition("SELECT UserId FROM crm.Users WHERE EntraOid = @oid", new { oid }, tx, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// One-time purge of demo data from the first seeder version, which recorded no run id. Only when the marker exists and the run id does not.
    /// Old definition: records created by a demo user (parents only when no row by anyone else still references them), FX rates last modified by
    /// the demo admin and matching a (currency, month) pair that <see cref="DemoGenerator"/> produces. Counts are logged first (a dry run stops there); everything happens in the caller's transaction; one audit row summarises it; the marker is removed.
    /// </summary>
    private async Task PurgeLegacyAsync(SqlConnection conn, SqlTransaction tx, DemoDataset data, Dictionary<string, string> settings, bool dryRun, CancellationToken ct)
    {
        if (!settings.TryGetValue(DemoMarker.MarkerKey, out var marker) || settings.ContainsKey(DemoMarker.RunIdKey))
        {
            throw new MigrationException($"--purge-legacy-demo applies only to demo data without a run id: '{DemoMarker.MarkerKey}' must exist and '{DemoMarker.RunIdKey}' must not. Nothing was changed.");
        }
        var oids = data.Users.Select(u => u.Oid).ToList();
        var ids = (await conn.QueryAsync<int>(new CommandDefinition("SELECT UserId FROM crm.Users WHERE EntraOid IN @oids", new { oids }, tx, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var adminOid = data.Users.First(u => u.Key == DemoMarker.AdminKey).Oid;
        var adminId = await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition("SELECT UserId FROM crm.Users WHERE EntraOid = @adminOid", new { adminOid }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (ids.Count == 0 || adminId is null)
        {
            throw new MigrationException("--purge-legacy-demo found no demo users; nothing to purge. Nothing was changed.");
        }
        // FX: only the (currency, month) pairs the generator produces, so a rate the demo admin edited by hand for another month is not touched.
        var p = new DynamicParameters();
        p.Add("ids", ids);
        p.Add("adminId", adminId.Value, DbType.Int32);
        var pairs = new StringBuilder();
        for (var i = 0; i < data.FxRates.Count; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            p.Add("c" + n, data.FxRates[i].Currency, DbType.AnsiStringFixedLength, size: 3);
            p.Add("m" + n, data.FxRates[i].Month.ToDateTime(TimeOnly.MinValue), DbType.Date);
            pairs.Append(i > 0 ? ", (@c" : "(@c").Append(n).Append(", @m").Append(n).Append(')');
        }
        var fxWhere = "BaseCurrency = 'DKK' AND ModifiedBy = @adminId AND EXISTS (SELECT 1 FROM (VALUES " + pairs + ") v(c, m) WHERE v.c = CurrencyCode AND v.m = RateMonth)";
        async Task<int> Count(string sql) => await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, p, tx, cancellationToken: ct)).ConfigureAwait(false);
        var counts = (
            Acc: await Count("SELECT COUNT(*) FROM crm.Accounts WHERE CreatedBy IN @ids").ConfigureAwait(false),
            Con: await Count("SELECT COUNT(*) FROM crm.Contacts WHERE CreatedBy IN @ids").ConfigureAwait(false),
            Opp: await Count("SELECT COUNT(*) FROM crm.Opportunities WHERE CreatedBy IN @ids").ConfigureAwait(false),
            Act: await Count("SELECT COUNT(*) FROM crm.Activities WHERE CreatedBy IN @ids").ConfigureAwait(false),
            Fx: await Count("SELECT COUNT(*) FROM crm.FxRates WHERE " + fxWhere).ConfigureAwait(false));
        output.WriteLine($"Legacy demo purge: found {counts.Acc} accounts, {counts.Con} contacts, {counts.Opp} opportunities, {counts.Act} activities and {counts.Fx} FX rates (created by demo users / last modified by the demo admin for the generated currency-months).");
        if (dryRun)
        {
            output.WriteLine("Dry run: nothing was changed. Run again with confirm (no --dry-run) to delete these rows.");
            return;
        }
        async Task<int> Del(string sql) => await conn.ExecuteAsync(new CommandDefinition(sql, p, tx, cancellationToken: ct)).ConfigureAwait(false);
        var act = await Del("DELETE FROM crm.Activities WHERE CreatedBy IN @ids").ConfigureAwait(false);
        var opp = await Del("DELETE o FROM crm.Opportunities o WHERE o.CreatedBy IN @ids AND NOT EXISTS (SELECT 1 FROM crm.Activities a WHERE a.OpportunityId = o.OpportunityId)").ConfigureAwait(false);
        var con = await Del("DELETE c FROM crm.Contacts c WHERE c.CreatedBy IN @ids AND NOT EXISTS (SELECT 1 FROM crm.Opportunities o WHERE o.PrimaryContactId = c.ContactId) AND NOT EXISTS (SELECT 1 FROM crm.Activities a WHERE a.ContactId = c.ContactId)").ConfigureAwait(false);
        var acc = await Del("DELETE a FROM crm.Accounts a WHERE a.CreatedBy IN @ids AND NOT EXISTS (SELECT 1 FROM crm.Contacts c WHERE c.AccountId = a.AccountId) AND NOT EXISTS (SELECT 1 FROM crm.Opportunities o WHERE o.AccountId = a.AccountId) AND NOT EXISTS (SELECT 1 FROM crm.Activities x WHERE x.AccountId = a.AccountId)").ConfigureAwait(false);
        var fx = await Del("DELETE FROM crm.FxRates WHERE " + fxWhere).ConfigureAwait(false);
        var settingId = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT SettingId FROM crm.Settings WHERE SettingKey = @k", new { k = DemoMarker.MarkerKey }, tx, cancellationToken: ct)).ConfigureAwait(false);
        var summary = $"Legacy demo purge: removed {acc} accounts, {con} contacts, {opp} opportunities, {act} activities, {fx} FX rates";
        await SetContextAsync(conn, tx, adminId.Value, "Admin", null, ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('setting', @sid, 'D', @k, @old, @new, @by, NULL, @by, @cid)",
            new { sid = settingId, k = DemoMarker.MarkerKey, old = marker, @new = summary, by = adminId.Value, cid = Guid.NewGuid() }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM crm.Settings WHERE SettingKey = @k", new { k = DemoMarker.MarkerKey }, tx, cancellationToken: ct)).ConfigureAwait(false);
        output.WriteLine($"{summary}. Removed the '{DemoMarker.MarkerKey}' marker; a normal seed-demo can run now.");
        var left = counts.Acc + counts.Con + counts.Opp + counts.Act - (acc + con + opp + act);
        if (left > 0) { output.WriteLine($"Note: {left} legacy demo records were kept because rows created by real users still reference them."); }
    }

    private static string Tracked(string entityType) =>
        // entityType is always a literal at the call sites below, never external input (no interpolation: SQL is concatenated from fixed fragments).
        "(SELECT EntityId FROM crm.AuditLog WHERE Action = 'C' AND CorrelationId = @runId AND EntityType = '" + entityType + "')";

    /// <summary>How many of the records the given run inserted (its 'C' audit rows) still exist.</summary>
    private static async Task<int> LoadTrackedAsync(SqlConnection conn, SqlTransaction tx, Guid runId, CancellationToken ct) =>
        await CountTrackedAsync(conn, tx, runId, ct).ConfigureAwait(false);

    private static async Task<int> CountTrackedAsync(SqlConnection conn, SqlTransaction tx, Guid runId, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT (SELECT COUNT(*) FROM crm.Accounts WHERE AccountId IN " + Tracked("account") + ")"
            + " + (SELECT COUNT(*) FROM crm.Contacts WHERE ContactId IN " + Tracked("contact") + ")"
            + " + (SELECT COUNT(*) FROM crm.Opportunities WHERE OpportunityId IN " + Tracked("opportunity") + ")"
            + " + (SELECT COUNT(*) FROM crm.Activities WHERE ActivityId IN " + Tracked("activity") + ")", new { runId }, tx, cancellationToken: ct)).ConfigureAwait(false);

    /// <summary>
    /// Removes exactly the records the previous run inserted (its audit rows), nothing else. A parent that something not inserted by the seeder still
    /// references (for example a note a person added to a demo account) is kept. Audit rows are append-only by design and stay.
    /// Demo users and teams stay too (audit rows reference them).
    /// </summary>
    private static async Task ResetAsync(SqlConnection conn, SqlTransaction tx, Guid? previousRun, Guid newRunId, bool dryRun, TextWriter output, CancellationToken ct)
    {
        if (previousRun is not { } runId)
        {
            return;
        }
        async Task<int> Del(string sql) => await conn.ExecuteAsync(new CommandDefinition(sql, new { runId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        var act = await Del("DELETE FROM crm.Activities WHERE ActivityId IN " + Tracked("activity")).ConfigureAwait(false);
        var opp = await Del("DELETE o FROM crm.Opportunities o WHERE o.OpportunityId IN " + Tracked("opportunity")
            + " AND NOT EXISTS (SELECT 1 FROM crm.Activities a WHERE a.OpportunityId = o.OpportunityId)").ConfigureAwait(false);
        var con = await Del("DELETE c FROM crm.Contacts c WHERE c.ContactId IN " + Tracked("contact")
            + " AND NOT EXISTS (SELECT 1 FROM crm.Opportunities o WHERE o.PrimaryContactId = c.ContactId)"
            + " AND NOT EXISTS (SELECT 1 FROM crm.Activities a WHERE a.ContactId = c.ContactId)").ConfigureAwait(false);
        var acc = await Del("DELETE a FROM crm.Accounts a WHERE a.AccountId IN " + Tracked("account")
            + " AND NOT EXISTS (SELECT 1 FROM crm.Contacts c WHERE c.AccountId = a.AccountId)"
            + " AND NOT EXISTS (SELECT 1 FROM crm.Opportunities o WHERE o.AccountId = a.AccountId)"
            + " AND NOT EXISTS (SELECT 1 FROM crm.Activities x WHERE x.AccountId = a.AccountId)").ConfigureAwait(false);
        // FX: only the (currency, month) rates this run inserted; a rate somebody else entered for the same month was never touched by the seeder.
        var fx = await Del("DELETE FROM crm.FxRates WHERE FxRateId IN " + Tracked("fxrate")).ConfigureAwait(false);
        // In a dry run the deletes above only measure; the caller never commits, so they are rolled back.
        output.WriteLine($"{(dryRun ? "Dry run (reset of run" : "Reset (run")} {runId}): {(dryRun ? "would remove" : "removed")} {acc} accounts, {con} contacts, {opp} opportunities, {act} activities and {fx} FX rates that the seeder inserted.");
        var left = await CountTrackedAsync(conn, tx, runId, ct).ConfigureAwait(false);
        if (left > 0)
        {
            output.WriteLine($"Note: {left} demo records {(dryRun ? "would be" : "were")} kept because rows created by real users still reference them.");
            if (!dryRun)
            {
                await CarryForwardAsync(conn, tx, runId, newRunId, ct).ConfigureAwait(false);
            }
        }
        if (dryRun)
        {
            return;
        }
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM crm.Settings WHERE SettingKey IN (@m, @r)", new { m = DemoMarker.MarkerKey, r = DemoMarker.RunIdKey }, tx, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Rows a reset keeps (a person's data still references them) stay demo rows: they get 'C' tracking rows under the new RunId, so the next
    /// reset finds them. Their original 'C' rows (old run) stay as they are.
    /// </summary>
    private static async Task CarryForwardAsync(SqlConnection conn, SqlTransaction tx, Guid oldRun, Guid newRun, CancellationToken ct)
    {
        foreach (var (type, table, idColumn) in new[] { ("account", "Accounts", "AccountId"), ("contact", "Contacts", "ContactId"), ("opportunity", "Opportunities", "OpportunityId"), ("activity", "Activities", "ActivityId") })
        {
            // type, table and column are literals from the array above, never external input.
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId) "
                + "SELECT '" + type + "', " + idColumn + ", 'C', N'CarriedForward', NULL, @note, OwnerId, TeamId, OwnerId, @newRun FROM crm.[" + table + "] WHERE " + idColumn + " IN " + Tracked(type),
                new { runId = oldRun, newRun, note = "kept by reset of run " + oldRun }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
    }

    private static async Task WriteAsync(SqlConnection conn, SqlTransaction tx, DemoDataset data, DemoOptions options, Guid runId, CancellationToken ct)
    {
        var teamIds = await EnsureTeamsAsync(conn, tx, data, ct).ConfigureAwait(false);
        var userIds = await EnsureUsersAsync(conn, tx, data, teamIds, ct).ConfigureAwait(false);
        await EnsureIndustryValuesAsync(conn, tx, ct).ConfigureAwait(false);
        var lookups = await LoadLookupsAsync(conn, tx, ct).ConfigureAwait(false);
        var ctx = new WriteContext(data, userIds, teamIds, lookups, runId);

        // Phases, each in the session context of the row owner: accounts and contacts, then opportunities (which may belong to another owner), then activities.
        foreach (var owner in data.Users.Where(u => u.Role != "Admin"))
        {
            await UseOwnerAsync(conn, tx, ctx, owner, ct).ConfigureAwait(false);
            await WriteAccountsAndContactsAsync(conn, tx, ctx, owner.Key, ct).ConfigureAwait(false);
        }
        foreach (var owner in data.Users.Where(u => u.Role != "Admin"))
        {
            await UseOwnerAsync(conn, tx, ctx, owner, ct).ConfigureAwait(false);
            await WriteOpportunitiesAsync(conn, tx, ctx, owner.Key, ct).ConfigureAwait(false);
        }
        foreach (var owner in data.Users.Where(u => u.Role != "Admin"))
        {
            await UseOwnerAsync(conn, tx, ctx, owner, ct).ConfigureAwait(false);
            await WriteActivitiesAsync(conn, tx, ctx, owner.Key, ct).ConfigureAwait(false);
        }

        var adminId = userIds[DemoMarker.AdminKey];
        await SetContextAsync(conn, tx, adminId, "Admin", null, ct).ConfigureAwait(false);
        await WriteFxAsync(conn, tx, ctx, adminId, ct).ConfigureAwait(false);
        if (options.DemoSettings)
        {
            await ApplyDemoSettingsAsync(conn, tx, adminId, runId, options, ct).ConfigureAwait(false);
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT crm.Settings (SettingKey, SettingValue, ModifiedBy) VALUES (@k, @v, @by), (@rk, @rv, @by)",
            new
            {
                k = DemoMarker.MarkerKey,
                v = $"v{DemoMarker.SchemaVersion.ToString(CultureInfo.InvariantCulture)} {data.Today:yyyy-MM-dd}",
                rk = DemoMarker.RunIdKey,
                rv = ctx.RunId.ToString(),
                by = adminId,
            }, tx, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// --demo-settings: visibility team, locale da-DK, and the demo currencies (NOK only for the edge variant). The generic industry values are NOT
    /// deactivated: that is a product-owner decision. Runs in the demo admin's context, like a change on the Settings pages.
    /// </summary>
    private static async Task ApplyDemoSettingsAsync(SqlConnection conn, SqlTransaction tx, int adminId, Guid runId, DemoOptions options, CancellationToken ct)
    {
        await SetContextAsync(conn, tx, adminId, "Admin", null, ct).ConfigureAwait(false);
        var values = new (string Key, string Value)[]
        {
            ("visibility.mode", "team"),
            ("locale.code", "da-DK"),
            ("currency.enabled", options.FxEdge ? "DKK,EUR,SEK,USD,NOK" : "DKK,EUR,SEK,USD"),
        };
        foreach (var (key, value) in values)
        {
            // Same audit as AdminRepo.SaveSettingsAsync: one row per setting that really changes ('C' when it is new, 'U' when the value differs).
            var cur = await conn.QuerySingleOrDefaultAsync<SettingRow2>(new CommandDefinition(
                "SELECT SettingId, SettingValue FROM crm.Settings WHERE SettingKey = @k", new { k = key }, tx, cancellationToken: ct)).ConfigureAwait(false);
            int id;
            string action;
            if (cur is null)
            {
                id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "INSERT crm.Settings (SettingKey, SettingValue, ModifiedBy) OUTPUT inserted.SettingId VALUES (@k, @v, @by)", new { k = key, v = value, by = adminId }, tx, cancellationToken: ct)).ConfigureAwait(false);
                action = "C";
            }
            else if (cur.SettingValue != value)
            {
                id = cur.SettingId;
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE crm.Settings SET SettingValue = @v, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE SettingId = @id", new { v = value, by = adminId, id }, tx, cancellationToken: ct)).ConfigureAwait(false);
                action = "U";
            }
            else
            {
                continue;
            }
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId) VALUES ('setting', @id, @act, @k, @old, @new, @by, NULL, @by, @cid)",
                new { id, act = action, k = key, old = cur?.SettingValue, @new = value, by = adminId, cid = runId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
    }

    private sealed record SettingRow2(int SettingId, string SettingValue);

    private sealed record WriteContext(DemoDataset Data, Dictionary<string, int> UserIds, Dictionary<string, int> TeamIds, Lookups Lookups, Guid RunId)
    {
        public Dictionary<string, int> AccountId { get; } = [];

        public Dictionary<string, int> ContactId { get; } = [];

        public Dictionary<string, int> OppId { get; } = [];

        public int? TeamOf(string ownerKey) => Data.Users.First(u => u.Key == ownerKey).TeamKey is { } t ? TeamIds[t] : null;
    }

    private static Task UseOwnerAsync(SqlConnection conn, SqlTransaction tx, WriteContext ctx, DemoUser owner, CancellationToken ct) =>
        SetContextAsync(conn, tx, ctx.UserIds[owner.Key], owner.Role, ctx.TeamOf(owner.Key), ct);

    /// <summary>Audit rows are written as the owner: ChangedBy = OwnerId = the record owner, ChangedAt = when the change happened.</summary>
    private static Task WriteAuditAsync(SqlConnection conn, SqlTransaction tx, WriteContext ctx, string ownerKey, List<object?[]> rows, CancellationToken ct) =>
        InsertAsync(conn, tx, "crm.AuditLog",
            ["EntityType", "EntityId", "Action", "FieldName", "OldValue", "NewValue", "OwnerId", "TeamId", "ChangedBy", "ChangedAt", "CorrelationId"], rows, false, ct);

    private static object?[] AuditRow(WriteContext ctx, string ownerKey, string type, int id, string action, string? field, string? oldValue, string? newValue, DateTime at) =>
        [type, id, action, field, oldValue, newValue, ctx.UserIds[ownerKey], ctx.TeamOf(ownerKey), ctx.UserIds[ownerKey], at, ctx.RunId];

    private static async Task WriteAccountsAndContactsAsync(SqlConnection conn, SqlTransaction tx, WriteContext ctx, string ownerKey, CancellationToken ct)
    {
        var ownerId = ctx.UserIds[ownerKey];
        var teamId = ctx.TeamOf(ownerKey);
        var accounts = ctx.Data.Accounts.Where(a => a.OwnerKey == ownerKey).ToList();
        var contacts = ctx.Data.Contacts.Where(c => c.OwnerKey == ownerKey).ToList();
        var audit = new List<object?[]>();

        var accountIds = await InsertAsync(conn, tx, "crm.Accounts",
            ["Name", "Website", "Phone", "IndustryValueId", "Street", "PostalCode", "City", "CountryCode", "Notes", "OwnerId", "TeamId", "CreatedBy", "CreatedAt", "ModifiedBy", "ModifiedAt"],
            accounts.Select(a => new object?[] { a.Name, a.Website, a.Phone, a.Industry is null ? null : ctx.Lookups.IndustryByLabel(a.Industry), a.Street, a.PostalCode, a.City, a.Country, a.Notes, ownerId, teamId, ownerId, a.CreatedAt, ownerId, a.CreatedAt }).ToList(), true, ct).ConfigureAwait(false);
        for (var i = 0; i < accounts.Count; i++)
        {
            ctx.AccountId[accounts[i].Key] = accountIds[i];
            audit.Add(AuditRow(ctx, ownerKey, "account", accountIds[i], "C", null, null, accounts[i].Name, accounts[i].CreatedAt));
        }

        var contactIds = await InsertAsync(conn, tx, "crm.Contacts",
            ["AccountId", "FirstName", "LastName", "JobTitle", "Email", "Phone", "Mobile", "OwnerId", "TeamId", "CreatedBy", "CreatedAt", "ModifiedBy", "ModifiedAt"],
            contacts.Select(c => new object?[] { c.AccountKey is null ? null : ctx.AccountId[c.AccountKey], c.FirstName, c.LastName, c.JobTitle, c.Email, c.Phone, c.Mobile, ownerId, teamId, ownerId, c.CreatedAt, ownerId, c.CreatedAt }).ToList(), true, ct).ConfigureAwait(false);
        for (var i = 0; i < contacts.Count; i++)
        {
            ctx.ContactId[contacts[i].Key] = contactIds[i];
            audit.Add(AuditRow(ctx, ownerKey, "contact", contactIds[i], "C", null, null, $"{contacts[i].FirstName} {contacts[i].LastName}".Trim(), contacts[i].CreatedAt));
        }
        await WriteAuditAsync(conn, tx, ctx, ownerKey, audit, ct).ConfigureAwait(false);
    }

    private static async Task WriteOpportunitiesAsync(SqlConnection conn, SqlTransaction tx, WriteContext ctx, string ownerKey, CancellationToken ct)
    {
        var lk = ctx.Lookups;
        var ownerId = ctx.UserIds[ownerKey];
        var teamId = ctx.TeamOf(ownerKey);
        var opps = ctx.Data.Opportunities.Where(o => o.OwnerKey == ownerKey).ToList();
        var audit = new List<object?[]>();
        int StageOf(int slot) => slot == DemoOpportunity.SlotWon ? lk.WonStage : slot == DemoOpportunity.SlotLost ? lk.LostStage : lk.OpenStages[Math.Min(slot, lk.OpenStages.Count - 1)];
        int? LostReason(string? label) => label is null ? null : lk.Pick("lost_reason", label) ?? lk.LostReasons.Cast<int?>().FirstOrDefault();
        var ids = await InsertAsync(conn, tx, "crm.Opportunities",
            ["Name", "AccountId", "PrimaryContactId", "StageId", "Amount", "CurrencyCode", "CloseDate", "LeadSourceValueId", "LostReasonValueId", "ClosedAt", "OwnerId", "TeamId", "CreatedBy", "CreatedAt", "ModifiedBy", "ModifiedAt"],
            opps.Select(o => new object?[]
            {
                o.Name, ctx.AccountId[o.AccountKey], o.ContactKey is null ? null : ctx.ContactId[o.ContactKey], StageOf(o.StageSlot), o.Amount, o.Currency, o.CloseDate,
                lk.Pick("lead_source", o.LeadSource), LostReason(o.LostReason), o.ClosedAt, ownerId, teamId, ownerId, o.CreatedAt, ownerId, o.ModifiedAt,
            }).ToList(), true, ct).ConfigureAwait(false);
        for (var i = 0; i < opps.Count; i++)
        {
            var o = opps[i];
            ctx.OppId[o.Key] = ids[i];
            audit.Add(AuditRow(ctx, ownerKey, "opportunity", ids[i], "C", null, null, o.Name, o.CreatedAt));
            foreach (var change in o.StageChanges)
            {
                var from = change.FromSlot < DemoOpportunity.SlotWon ? lk.OpenStages[Math.Min(change.FromSlot, lk.OpenStages.Count - 1)] : StageOf(change.FromSlot);
                audit.Add(AuditRow(ctx, ownerKey, "opportunity", ids[i], "U", "StageId", lk.StageNames[from], lk.StageNames[StageOf(change.ToSlot)], change.At));
            }
            if (o.PreviousOwnerKey is not null && o.OwnerChangedAt is { } at)
            {
                audit.Add(AuditRow(ctx, ownerKey, "opportunity", ids[i], "U", "OwnerId", ctx.UserIds[o.PreviousOwnerKey].ToString(CultureInfo.InvariantCulture), ownerId.ToString(CultureInfo.InvariantCulture), at));
            }
        }
        await WriteAuditAsync(conn, tx, ctx, ownerKey, audit, ct).ConfigureAwait(false);
    }

    private static async Task WriteActivitiesAsync(SqlConnection conn, SqlTransaction tx, WriteContext ctx, string ownerKey, CancellationToken ct)
    {
        var ownerId = ctx.UserIds[ownerKey];
        var teamId = ctx.TeamOf(ownerKey);
        var acts = ctx.Data.Activities.Where(a => a.OwnerKey == ownerKey).ToList();
        var ids = await InsertAsync(conn, tx, "crm.Activities",
            ["ActivityType", "Subject", "Description", "AccountId", "ContactId", "OpportunityId", "DueDate", "DueTime", "IsDone", "DoneAt", "OwnerId", "TeamId", "CreatedBy", "CreatedAt", "ModifiedBy", "ModifiedAt"],
            acts.Select(a => new object?[]
            {
                a.Type, a.Subject, a.Description, ctx.AccountId[a.AccountKey], a.ContactKey is null ? null : ctx.ContactId[a.ContactKey], a.OpportunityKey is null ? null : ctx.OppId[a.OpportunityKey],
                a.DueDate, a.DueTime, a.IsDone, a.DoneAt, ownerId, teamId, ownerId, a.CreatedAt, ownerId, a.ModifiedAt,
            }).ToList(), true, ct).ConfigureAwait(false);
        var audit = acts.Select((a, i) => AuditRow(ctx, ownerKey, "activity", ids[i], "C", null, null, a.Subject, a.CreatedAt)).ToList();
        await WriteAuditAsync(conn, tx, ctx, ownerKey, audit, ct).ConfigureAwait(false);
    }

    /// <summary>Segments are 'industry' picklist values with Codes wind / building / industry (added when missing; existing values are never changed or deactivated).</summary>
    private static async Task EnsureIndustryValuesAsync(SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        foreach (var (code, label) in DemoCatalog.IndustryValues)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                DECLARE @p int = (SELECT PicklistId FROM crm.Picklists WHERE Code = 'industry');
                IF @p IS NOT NULL AND NOT EXISTS (SELECT 1 FROM crm.PicklistValues WHERE PicklistId = @p AND (Code = @c OR Label = @l))
                  INSERT crm.PicklistValues (PicklistId, Code, Label, SortOrder)
                  VALUES (@p, @c, @l, (SELECT ISNULL(MAX(SortOrder), 0) + 1 FROM crm.PicklistValues WHERE PicklistId = @p))
                """, new { c = code, l = label }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, int>> EnsureTeamsAsync(SqlConnection conn, SqlTransaction tx, DemoDataset data, CancellationToken ct)
    {
        foreach (var t in data.Teams)
        {
            var clash = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM crm.Teams WHERE Name = @n AND EntraGroupId <> @g", new { n = t.Name, g = t.GroupId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (clash > 0)
            {
                throw new MigrationException($"A team named '{t.Name}' already exists and is not a demo team. Rename it first; seed-demo never changes existing teams.");
            }
            await conn.ExecuteAsync(new CommandDefinition(
                "IF NOT EXISTS (SELECT 1 FROM crm.Teams WHERE EntraGroupId = @g) INSERT crm.Teams (Name, EntraGroupId) VALUES (@n, @g)",
                new { n = t.Name, g = t.GroupId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
        var rows = await conn.QueryAsync<TeamRow>(new CommandDefinition("SELECT TeamId, EntraGroupId FROM crm.Teams WHERE EntraGroupId IN @g", new { g = data.Teams.Select(t => t.GroupId).ToList() }, tx, cancellationToken: ct)).ConfigureAwait(false);
        var byGroup = rows.ToDictionary(r => r.EntraGroupId, r => r.TeamId);
        return data.Teams.ToDictionary(t => t.Key, t => byGroup[t.GroupId]);
    }

    private static async Task<Dictionary<string, int>> EnsureUsersAsync(SqlConnection conn, SqlTransaction tx, DemoDataset data, Dictionary<string, int> teamIds, CancellationToken ct)
    {
        var i = 0;
        foreach (var u in data.Users)
        {
            i++;
            var teamId = u.TeamKey is null ? (int?)null : teamIds[u.TeamKey];
            await conn.ExecuteAsync(new CommandDefinition(
                """
                IF NOT EXISTS (SELECT 1 FROM crm.Users WHERE EntraOid = @oid)
                  INSERT crm.Users (EntraOid, TenantId, DisplayName, Email, Role, TeamId, TeamStatus, IsActive, LastSignInAt, CreatedAt, ModifiedAt)
                  VALUES (@oid, @tenant, @name, @email, @role, @team, @status, @active, @signIn, @created, @created)
                """,
                new
                {
                    oid = u.Oid,
                    tenant = Guid.Parse(ZeroTenant),
                    name = u.Name,
                    email = u.Email,
                    role = u.Role,
                    team = teamId,
                    status = teamId is null ? "none" : "ok",
                    active = u.Active,
                    signIn = data.Today.AddDays(-i).ToDateTime(new TimeOnly(9, 0)),
                    created = data.Today.AddDays(-700).ToDateTime(new TimeOnly(9, 0)),
                }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
        var rows = await conn.QueryAsync<UserRow>(new CommandDefinition("SELECT UserId, EntraOid, TeamId FROM crm.Users WHERE EntraOid IN @oids", new { oids = data.Users.Select(u => u.Oid).ToList() }, tx, cancellationToken: ct)).ConfigureAwait(false);
        var byOid = rows.ToDictionary(r => r.EntraOid, r => r.UserId);
        return data.Users.ToDictionary(u => u.Key, u => byOid[u.Oid]);
    }

    private sealed record Lookups(IReadOnlyList<int> OpenStages, int WonStage, int LostStage, IReadOnlyDictionary<int, string> StageNames, Func<string, string, int?> Pick, IReadOnlyList<int> LostReasons)
    {
        public int? IndustryByLabel(string label) => Pick("industry", label);
    }

    private static async Task<Lookups> LoadLookupsAsync(SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        var stages = (await conn.QueryAsync<StageRow>(new CommandDefinition("SELECT StageId, Name, StageType FROM crm.PipelineStages ORDER BY SortOrder, StageId", transaction: tx, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var open = stages.Where(s => s.StageType == "open").Select(s => s.StageId).ToList();
        var won = stages.FirstOrDefault(s => s.StageType == "won")?.StageId;
        var lost = stages.FirstOrDefault(s => s.StageType == "lost")?.StageId;
        if (open.Count == 0 || won is null || lost is null)
        {
            throw new MigrationException("seed-demo needs at least one open, one won and one lost pipeline stage.");
        }
        var picks = (await conn.QueryAsync<PickRow>(new CommandDefinition(
            "SELECT p.Code AS PCode, v.ValueId, v.Label FROM crm.PicklistValues v JOIN crm.Picklists p ON p.PicklistId = v.PicklistId WHERE v.IsActive = 1", transaction: tx, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        int? Pick(string code, string label) => picks.FirstOrDefault(p => p.PCode == code && p.Label == label)?.ValueId;
        return new Lookups(open, won.Value, lost.Value, stages.ToDictionary(s => s.StageId, s => s.Name), Pick, picks.Where(p => p.PCode == "lost_reason").Select(p => p.ValueId).ToList());
    }

    private static async Task WriteFxAsync(SqlConnection conn, SqlTransaction tx, WriteContext ctx, int adminId, CancellationToken ct)
    {
        // Never overwrite a rate somebody else entered for the same month. Only inserted rates come back with an id and get tracked.
        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM crm.FxRates WHERE BaseCurrency = 'DKK' AND CurrencyCode = @c AND RateMonth = @m)
            BEGIN
              INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy, ModifiedAt) VALUES ('DKK', @c, @m, @r, @by, @at);
              SELECT CAST(SCOPE_IDENTITY() AS int);
            END
            """;
        var audit = new List<object?[]>();
        foreach (var r in ctx.Data.FxRates)
        {
            var p = new DynamicParameters();
            p.Add("c", r.Currency, DbType.AnsiStringFixedLength, size: 3);
            p.Add("m", r.Month.ToDateTime(TimeOnly.MinValue), DbType.Date);
            p.Add("r", r.Rate, DbType.Decimal, precision: 19, scale: 8);
            p.Add("by", adminId, DbType.Int32);
            p.Add("at", r.Month.ToDateTime(new TimeOnly(8, 0)), DbType.DateTime2);
            var id = await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(sql, p, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (id is { } inserted)
            {
                // The tracked key is (currency, month); the id pins the exact row so a rate somebody re-enters later is never mistaken for ours.
                audit.Add(AuditRow(ctx, DemoMarker.AdminKey, "fxrate", inserted, "C", "CurrencyMonth", null, $"{r.Currency} {r.Month:yyyy-MM}", r.Month.ToDateTime(new TimeOnly(8, 0))));
            }
        }
        await WriteAuditAsync(conn, tx, ctx, DemoMarker.AdminKey, audit, ct).ConfigureAwait(false);
    }

    /// <summary>Multi-row insert in chunks. With <paramref name="wantIds"/> the new identity values come back in row order.</summary>
    private static async Task<List<int>> InsertAsync(SqlConnection conn, SqlTransaction tx, string table, string[] columns, IReadOnlyList<object?[]> rows, bool wantIds, CancellationToken ct)
    {
        var ids = new List<int>();
        // table and columns are literals at the call sites above, never external input.
        var head = string.Concat("INSERT ", table, " (", string.Join(", ", columns), ") VALUES (");
        for (var offset = 0; offset < rows.Count; offset += ChunkSize)
        {
            var sql = new StringBuilder("DECLARE @ids TABLE (i int NOT NULL, id int NOT NULL);\n");
            var p = new DynamicParameters();
            var n = Math.Min(ChunkSize, rows.Count - offset);
            for (var r = 0; r < n; r++)
            {
                var row = rows[offset + r];
                sql.Append(head);
                for (var c = 0; c < columns.Length; c++)
                {
                    var name = $"p{r.ToString(CultureInfo.InvariantCulture)}_{c.ToString(CultureInfo.InvariantCulture)}";
                    AddParam(p, name, row[c]);
                    sql.Append(c > 0 ? ", @" : "@").Append(name);
                }
                sql.Append(");\n");
                if (wantIds)
                {
                    sql.Append("INSERT @ids VALUES (").Append(r).Append(", CAST(SCOPE_IDENTITY() AS int));\n");
                }
            }
            if (wantIds)
            {
                sql.Append("SELECT id FROM @ids ORDER BY i;");
                ids.AddRange(await conn.QueryAsync<int>(new CommandDefinition(sql.ToString(), p, tx, cancellationToken: ct)).ConfigureAwait(false));
            }
            else
            {
                await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), p, tx, cancellationToken: ct)).ConfigureAwait(false);
            }
        }
        if (wantIds && ids.Count != rows.Count)
        {
            throw new MigrationException($"Inserting into {table} returned {ids.Count} ids for {rows.Count} rows.");
        }
        return ids;
    }

    private static void AddParam(DynamicParameters p, string name, object? v)
    {
        switch (v)
        {
            case null: p.Add(name, null, DbType.String); break;
            case string s: p.Add(name, s, DbType.String); break;
            case int i: p.Add(name, i, DbType.Int32); break;
            case bool b: p.Add(name, b, DbType.Boolean); break;
            case decimal d: p.Add(name, d, DbType.Decimal, precision: 19, scale: 2); break;
            case DateTime dt: p.Add(name, dt, DbType.DateTime2); break;
            case DateOnly d: p.Add(name, d.ToDateTime(TimeOnly.MinValue), DbType.Date); break;
            case TimeOnly t: p.Add(name, t.ToTimeSpan(), DbType.Time); break;
            case Guid g: p.Add(name, g, DbType.Guid); break;
            default: throw new ArgumentOutOfRangeException(nameof(v), v.GetType().Name, "Unsupported parameter type");
        }
    }
}
