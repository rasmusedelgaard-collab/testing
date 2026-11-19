using System.Data;
using System.Reflection;
using System.Text;
using Crm.Web.Config;
using Microsoft.Data.SqlClient;

namespace Crm.Web.Data;

/// <summary>
/// In-app migrator (port of scripts/migrate.ts, runSqlFile.ts, testAdminSeed.ts). Compatible with the existing
/// dbo.SchemaVersions table and checksums. Runs as the deployer / DB owner identity, never as the app user.
/// </summary>
public sealed class Migrator(SqlConnectionFactory factory, TextWriter output, TextWriter error)
{
    private const string Bootstrap = """
        IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL
          CREATE TABLE dbo.SchemaVersions (
            Version   int           NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
            Name      nvarchar(200) NOT NULL,
            Checksum  char(64)      NOT NULL,
            AppliedAt datetime2(3)  NOT NULL CONSTRAINT DF_SchemaVersions_AppliedAt DEFAULT SYSUTCDATETIME()
          );
        """;

    private const string TestAdminUpsert = """
        MERGE crm.Users AS u
        USING (SELECT @oid AS EntraOid) AS s ON u.EntraOid = s.EntraOid
        WHEN MATCHED THEN UPDATE SET DisplayName = @name, Email = @email, Role = 'Admin', TeamId = NULL, TeamStatus = 'none', IsActive = 1, ModifiedAt = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN INSERT (EntraOid, TenantId, DisplayName, Email, Role, TeamId, TeamStatus)
          VALUES (@oid, '00000000-0000-0000-0000-000000000000', @name, @email, 'Admin', NULL, 'none');
        """;

    private const string TestMarkerUpsert = """
        EXEC sp_set_session_context N'UserId', 0, @read_only = 0;
        EXEC sp_set_session_context N'Role', N'Admin', @read_only = 0;
        IF NOT EXISTS (SELECT 1 FROM crm.Settings WHERE SettingKey = 'environment')
          INSERT crm.Settings (SettingKey, SettingValue) VALUES ('environment', N'test')
        ELSE
          UPDATE crm.Settings SET SettingValue = N'test', ModifiedAt = SYSUTCDATETIME() WHERE SettingKey = 'environment' AND SettingValue <> N'test';
        EXEC sp_set_session_context N'UserId', NULL, @read_only = 0;
        EXEC sp_set_session_context N'Role', NULL, @read_only = 0;
        """;

    /// <summary>Embedded resources: migrations/NNN_name.sql and seed/test-users.sql.</summary>
    public static IReadOnlyList<(string File, byte[] Bytes)> EmbeddedMigrations()
    {
        var asm = typeof(Migrator).Assembly;
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("migrations/", StringComparison.Ordinal))
            .Select(n => (File: n["migrations/".Length..], Bytes: ReadResource(asm, n)))
            .Where(x => SqlBatches.ParseMigrationName(x.File) is not null)
            .OrderBy(x => x.File, StringComparer.Ordinal)
            .ToList();
    }

    private static byte[] ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing embedded resource " + name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public async Task<int> MigrateAsync(CancellationToken ct) =>
        await GuardedAsync(async () =>
        {
            await using var conn = await factory.OpenMigratorAsync(ct).ConfigureAwait(false);
            await MigrateAsync(conn, EmbeddedMigrations(), ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

    public async Task MigrateAsync(SqlConnection conn, IReadOnlyList<(string File, byte[] Bytes)> files, CancellationToken ct)
    {
        await ExecBatchAsync(conn, null, Bootstrap, ct).ConfigureAwait(false);
        var applied = new Dictionary<int, string>();
        await using (var cmd = new SqlCommand("SELECT Version, Checksum FROM dbo.SchemaVersions", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                applied[r.GetInt32(0)] = r.GetString(1).Trim();
            }
        }
        foreach (var (file, bytes) in files.OrderBy(f => f.File, StringComparer.Ordinal))
        {
            var meta = SqlBatches.ParseMigrationName(file)!.Value;
            var sum = SqlBatches.Checksum(bytes);
            if (applied.TryGetValue(meta.Version, out var existing))
            {
                if (existing != sum)
                {
                    throw new MigrationException($"Migration {file} was changed after it was applied (checksum mismatch). Add a new migration instead.");
                }
                output.WriteLine($"skip   {file}");
                continue;
            }
            output.WriteLine($"apply  {file}");
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var batch in SqlBatches.Split(Encoding.UTF8.GetString(bytes)))
                {
                    await ExecBatchAsync(conn, tx, batch, ct).ConfigureAwait(false);
                }
                await using var ins = new SqlCommand("INSERT INTO dbo.SchemaVersions (Version, Name, Checksum) VALUES (@v, @n, @c)", conn, tx);
                ins.Parameters.Add(new SqlParameter("@v", SqlDbType.Int) { Value = meta.Version });
                ins.Parameters.Add(new SqlParameter("@n", SqlDbType.NVarChar, 200) { Value = meta.Name });
                ins.Parameters.Add(new SqlParameter("@c", SqlDbType.Char, 64) { Value = sum });
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch (SqlException e)
            {
                try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { /* already gone */ }
                throw new MigrationException($"Migration {file} failed and was rolled back: SQL error {e.Number}: {e.Message}");
            }
        }
        await AssertPoliciesEnabledAsync(conn, ct).ConfigureAwait(false);
    }

    private async Task AssertPoliciesEnabledAsync(SqlConnection conn, CancellationToken ct)
    {
        var enabled = new HashSet<string>();
        await using (var cmd = new SqlCommand("SELECT name, is_enabled FROM sys.security_policies WHERE name IN (N'CrmRowPolicy', N'CrmConfigPolicy')", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (r.GetBoolean(1))
                {
                    enabled.Add(r.GetString(0));
                }
            }
        }
        var missing = new[] { "CrmRowPolicy", "CrmConfigPolicy" }.Where(n => !enabled.Contains(n)).ToList();
        if (missing.Count > 0)
        {
            throw new MigrationException($"Security policy not enabled or missing: {string.Join(", ", missing)}. Refusing to finish: data would be unprotected.");
        }
        output.WriteLine("Security policies CrmRowPolicy and CrmConfigPolicy are enabled.");
    }

    public async Task<int> SeedTestAsync(TestAdmin? admin, CancellationToken ct) =>
        await GuardedAsync(async () =>
        {
            await using var conn = await factory.OpenMigratorAsync(ct).ConfigureAwait(false);
            var asm = typeof(Migrator).Assembly;
            var text = Encoding.UTF8.GetString(ReadResource(asm, "seed/test-users.sql"));
            foreach (var batch in SqlBatches.Split(text))
            {
                await ExecBatchAsync(conn, null, batch, ct).ConfigureAwait(false);
            }
            output.WriteLine("Seeded test users and teams.");
            // The test marker: seed-demo refuses any database without it. Written under the migration-scoped Admin context (UserId 0), like 004_seed.sql.
            await ExecBatchAsync(conn, null, TestMarkerUpsert, ct).ConfigureAwait(false);
            output.WriteLine("Set the test-environment marker (Settings environment=test).");
            if (admin is not null)
            {
                await using var cmd = new SqlCommand(TestAdminUpsert, conn);
                cmd.Parameters.Add(new SqlParameter("@oid", SqlDbType.UniqueIdentifier) { Value = Guid.Parse(admin.Oid) });
                cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 200) { Value = admin.Name });
                cmd.Parameters.Add(new SqlParameter("@email", SqlDbType.NVarChar, 254) { Value = admin.Email });
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                output.WriteLine("Seeded the named test Admin (see TEST_ADMIN_*; the test-login picker offers this user).");
            }
        }).ConfigureAwait(false);

    /// <summary>seed-demo (the safety gate lives in CliRunner). Test databases only; see <see cref="DemoSeeder"/>.</summary>
    public async Task<int> SeedDemoAsync(bool reset, TimeProvider time, CancellationToken ct, bool fxEdge = false, bool demoSettings = false, bool purgeLegacy = false, bool dryRun = false) =>
        await GuardedAsync(async () =>
        {
            await using var conn = await factory.OpenMigratorAsync(ct).ConfigureAwait(false);
            await new DemoSeeder(time, output).RunAsync(conn, reset, new Crm.Web.Services.Demo.DemoOptions(fxEdge, demoSettings, purgeLegacy, dryRun), ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

    /// <summary>Port of runSqlFile.ts: batches run on one connection; failures are reported and the run continues.</summary>
    public async Task<int> RunSqlFileAsync(string file, CancellationToken ct)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(ResolveFile(file), ct).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            error.WriteLine("Error: " + e.Message);
            return 1;
        }
        var failed = 0;
        try
        {
            await using var conn = await factory.OpenMigratorAsync(ct).ConfigureAwait(false);
            var batches = SqlBatches.Split(text);
            for (var i = 0; i < batches.Count; i++)
            {
                try
                {
                    await using var cmd = new SqlCommand(batches[i], conn);
                    await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    do
                    {
                        failed += await PrintRowsAsync(r, ct).ConfigureAwait(false);
                    }
                    while (await r.NextResultAsync(ct).ConfigureAwait(false));
                }
                catch (SqlException e)
                {
                    failed++;
                    error.WriteLine(DescribeBatchError(e, i + 1, batches.Count, batches[i]));
                }
            }
        }
        catch (SqlException e)
        {
            error.WriteLine($"Error: could not connect or run (SQL error {e.Number})");
            return 1;
        }
        if (failed > 0)
        {
            error.WriteLine($"{failed} case(s) FAILED");
            return 1;
        }
        output.WriteLine("No FAIL rows.");
        return 0;
    }

    /// <summary>
    /// A relative path is taken from the current directory; if it is not there (`dotnet run --project` starts in the project
    /// folder), parent directories are tried, so `run-sql db/tests/x.sql` works from the repository root.
    /// </summary>
    internal static string ResolveFile(string file)
    {
        if (Path.IsPathRooted(file) || File.Exists(file))
        {
            return Path.GetFullPath(file);
        }
        for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return Path.GetFullPath(file);
    }

    private async Task<int> PrintRowsAsync(SqlDataReader r, CancellationToken ct)
    {
        var failed = 0;
        var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
        var resultIx = cols.FindIndex(c => c == "Result");
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            object? Col(string name)
            {
                var ix = cols.IndexOf(name);
                return ix < 0 || r.IsDBNull(ix) ? null : r.GetValue(ix);
            }
            if (resultIx >= 0)
            {
                var result = Convert.ToString(r.GetValue(resultIx), System.Globalization.CultureInfo.InvariantCulture);
                if (result != "PASS")
                {
                    failed++;
                }
                output.WriteLine($"{Col("Case"),-8} {result,-5} {Col("Description")}{(result == "PASS" ? "" : " | " + Col("Detail"))}");
            }
            else
            {
                output.WriteLine(string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : r.GetValue(i))));
            }
        }
        return failed;
    }

    public static string DescribeBatchError(SqlException e, int n, int total, string batch)
    {
        var lines = batch.Split('\n');
        var text = e.LineNumber >= 1 && e.LineNumber <= lines.Length ? lines[e.LineNumber - 1].Trim() : null;
        var first = lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal));
        var sb = new StringBuilder();
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"Batch {n}/{total} FAILED: {e.Message}\n");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"  SQL error {e.Number} state {e.State}, line {e.LineNumber} of the batch{(string.IsNullOrEmpty(e.Procedure) ? "" : ", in " + e.Procedure)}");
        if (text is { Length: > 0 }) { sb.Append("\n  at: ").Append(text.Length > 200 ? text[..200] : text); }
        if (first is { Length: > 0 }) { sb.Append("\n  batch starts with: ").Append(first.Length > 100 ? first[..100] : first); }
        return sb.ToString();
    }

    private static async Task ExecBatchAsync(SqlConnection conn, SqlTransaction? tx, string batch, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(batch, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<int> GuardedAsync(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
            return 0;
        }
        catch (MigrationException e)
        {
            error.WriteLine(e.Message);
            return 1;
        }
        catch (SqlException e)
        {
            error.WriteLine($"Error: SQL error {e.Number}: {e.Message}");
            return 1;
        }
    }
}

public sealed class MigrationException(string message) : Exception(message);
