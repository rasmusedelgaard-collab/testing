using Crm.Web.Config;
using Microsoft.Data.SqlClient;

namespace Crm.Tests;

/// <summary>
/// Real-SQL test gate. CRM_TEST_SQL_SA is an SA connection string (or just the SA password, for localhost,1433).
/// Without it SQL/HTTP tests are skipped locally; with CRM_REQUIRE_SQL=true (CI) they run and FAIL if SQL is unreachable.
/// </summary>
public static class SqlEnv
{
    public static string? Raw => Environment.GetEnvironmentVariable("CRM_TEST_SQL_SA");
    public static bool Required => string.Equals(Environment.GetEnvironmentVariable("CRM_REQUIRE_SQL"), "true", StringComparison.OrdinalIgnoreCase);
    public static bool Configured => !string.IsNullOrWhiteSpace(Raw);

    /// <summary>Used by SkipUnless: run when configured, or when required (so a missing SQL fails instead of skipping).</summary>
    public const string SkipMessage = "No SQL Server configured (set CRM_TEST_SQL_SA); CI sets CRM_REQUIRE_SQL=true so these never skip there.";

    public static bool ShouldRun => Configured || Required;

    public static SqlConnectionStringBuilder SaBuilder()
    {
        var raw = Raw ?? throw new InvalidOperationException("CRM_REQUIRE_SQL is set but CRM_TEST_SQL_SA is not.");
        if (raw.Contains('=', StringComparison.Ordinal))
        {
            return new SqlConnectionStringBuilder(raw);
        }
        return new SqlConnectionStringBuilder { DataSource = "localhost,1433", UserID = "sa", Password = raw, TrustServerCertificate = true, Encrypt = SqlConnectionEncryptOption.Mandatory };
    }

    public static DbConfig ToDbConfig(SqlConnectionStringBuilder b, string database, string? user = null, string? password = null)
    {
        var ds = b.DataSource.Replace("tcp:", "", StringComparison.OrdinalIgnoreCase);
        var parts = ds.Split(',');
        int? port = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : null;
        return new DbConfig(parts[0], port, database, DbAuthMode.SqlPassword, user ?? b.UserID, password ?? b.Password, b.TrustServerCertificate, 10);
    }
}

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null, [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = SqlEnv.SkipMessage;
        SkipUnless = nameof(SqlEnv.ShouldRun);
        SkipType = typeof(SqlEnv);
    }
}

public sealed class SqlTheoryAttribute : TheoryAttribute
{
    public SqlTheoryAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null, [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = SqlEnv.SkipMessage;
        SkipUnless = nameof(SqlEnv.ShouldRun);
        SkipType = typeof(SqlEnv);
    }
}
