using System.Text.RegularExpressions;

namespace Crm.Tests.Architecture;

/// <summary>Source-scan rules (dotnet.md 3.2), like importRestrictions.test.ts.</summary>
public sealed partial class SourceRulesTests
{
    private static readonly string WebRoot = FindWebRoot();

    private static string FindWebRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "Crm.Web");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Crm.Web.csproj")))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException("src/Crm.Web not found");
    }

    private static IEnumerable<string> Files(string pattern) =>
        Directory.EnumerateFiles(WebRoot, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Rel(string f) => Path.GetRelativePath(WebRoot, f).Replace('\\', '/');

    private static IEnumerable<string> Cs() => Files("*.cs");

    [Fact]
    public void SqlClient_and_Dapper_only_under_Data()
    {
        var bad = Cs().Where(f => !Rel(f).StartsWith("Data/", StringComparison.Ordinal) && Regex.IsMatch(File.ReadAllText(f), @"using\s+(Microsoft\.Data\.SqlClient|Dapper)\b"))
            .Select(Rel).ToList();
        // Config/ and Infrastructure/ must not touch the driver directly
        Assert.Empty(bad);
    }

    [Fact]
    public void Open_methods_are_referenced_only_by_their_owners()
    {
        foreach (var f in Cs())
        {
            var name = Path.GetFileName(f);
            var t = File.ReadAllText(f);
            if (name != "SqlConnectionFactory.cs")
            {
                if (name != "RlsDb.cs") { Assert.DoesNotContain("OpenRlsAsync", t, StringComparison.Ordinal); }
                if (name != "SystemDb.cs") { Assert.DoesNotContain("OpenSystemAsync", t, StringComparison.Ordinal); }
                if (name != "Migrator.cs") { Assert.DoesNotContain("OpenMigratorAsync", t, StringComparison.Ordinal); }
            }
        }
    }

    [Fact]
    public void New_SqlConnection_only_in_factory_and_migrator() =>
        Assert.Empty(Cs().Where(f => Path.GetFileName(f) is not ("SqlConnectionFactory.cs" or "Migrator.cs") && File.ReadAllText(f).Contains("new SqlConnection(", StringComparison.Ordinal)).Select(Rel));

    [Fact]
    public void Environment_and_raw_configuration_reads_only_in_Config()
    {
        var bad = Cs().Where(f => !Rel(f).StartsWith("Config/", StringComparison.Ordinal) && Rel(f) != "Program.cs")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"Environment\.GetEnvironmentVariable|\bconfiguration\[|\bConfiguration\["))
            .Select(Rel).ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void Program_reads_only_the_documented_host_keys()
    {
        var keys = Regex.Matches(File.ReadAllText(Path.Combine(WebRoot, "Program.cs")), @"Configuration\[""([A-Z_]+)""\]").Select(m => m.Groups[1].Value).Distinct().Order().ToList();
        Assert.Equal(["APP_POOL_ID", "WEBSITE_INSTANCE_ID"], keys);
    }

    [Fact]
    public void No_Html_Raw_in_views_without_review() => Assert.Empty(Files("*.cshtml").Where(f => File.ReadAllText(f).Contains("Html.Raw(", StringComparison.Ordinal)).Select(Rel));

    [Fact]
    public void Views_have_no_inline_script_or_style_attributes()
    {
        foreach (var f in Files("*.cshtml"))
        {
            var t = File.ReadAllText(f);
            Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)(?![^>]*type=""application/json"")", t);
            Assert.DoesNotMatch(@"\sstyle\s*=", t);
            Assert.DoesNotContain("javascript:", t, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"\son[a-z]+\s*=", t);
        }
    }

    [Fact]
    public void Services_do_not_reference_AspNetCore_or_Data()
    {
        foreach (var f in Files("*.cs").Where(f => Rel(f).StartsWith("Services/", StringComparison.Ordinal)))
        {
            var t = File.ReadAllText(f);
            Assert.DoesNotContain("Microsoft.AspNetCore", t, StringComparison.Ordinal);
            Assert.DoesNotContain("Crm.Web.Data", t, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Time_comes_from_TimeProvider()
    {
        var bad = Cs().Where(f => Regex.IsMatch(File.ReadAllText(f), @"DateTime(Offset)?\.(UtcNow|Now)\b")).Select(Rel).ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void Sql_is_parameterised_no_interpolated_user_input_in_Data()
    {
        // Interpolation is only allowed for the constant fragments declared in Data (Base, Reserved) and test-user queries.
        var hits = Files("*.cs").Where(f => Rel(f).StartsWith("Data/", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, l, i)))
            .Where(x => Regex.IsMatch(x.l, @"\$""\s*(SELECT|INSERT|UPDATE|DELETE|EXEC)\b", RegexOptions.IgnoreCase))
            .Select(x => $"{Rel(x.f)}:{x.i + 1}").ToList();
        Assert.Empty(hits);
    }
}
