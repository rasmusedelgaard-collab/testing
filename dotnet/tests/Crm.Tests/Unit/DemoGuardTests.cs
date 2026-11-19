using Crm.Web.Config;
using Microsoft.Extensions.Configuration;

namespace Crm.Tests.Unit;

/// <summary>seed-demo safety gate: refused on live/Azure hosts, needs --yes-demo, no unknown options, and the target must say "test deployment" (M-1).</summary>
public sealed class DemoGuardTests
{
    private static IConfiguration Env(params (string, string)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Item1, p => (string?)p.Item2)).Build();

    private static readonly (string, string)[] TestTarget = [("ALLOW_DEMO_SEED", "true"), ("DB_AUTH_MODE", "sql-password"), ("AUTH_MODE", "test-access-code")];

    private static IConfiguration TargetEnv(params (string, string)[] more) => Env([.. TestTarget, .. more]);

    private static IConfiguration TargetWithout(string key) => Env(TestTarget.Where(p => p.Item1 != key).ToArray());

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    public void Live_in_any_spelling_is_refused(string live)
    {
        var error = CliRunner.DemoArgsError(["seed-demo", "--yes-demo"], TargetEnv(("LIVE", live)));
        Assert.Contains("LIVE", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WEBSITE_INSTANCE_ID")]
    [InlineData("WEBSITE_SITE_NAME")]
    public void Azure_app_service_is_refused(string key)
    {
        Assert.Contains("Azure", CliRunner.DemoArgsError(["seed-demo", "--yes-demo"], TargetEnv((key, "abc"))), StringComparison.Ordinal);
    }

    [Fact]
    public void The_explicit_flag_is_required_and_unknown_options_are_rejected()
    {
        Assert.Contains("--yes-demo", CliRunner.DemoArgsError(["seed-demo"], Env()), StringComparison.Ordinal);
        Assert.Contains("--yes-demo", CliRunner.DemoArgsError(["seed-demo", "--reset-demo"], Env()), StringComparison.Ordinal);
        Assert.Contains("usage", CliRunner.DemoArgsError(["seed-demo", "--yes-demo", "--force"], Env()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--yes-demo")]
    [InlineData("--reset-demo")]
    [InlineData("--edge-fx")]
    [InlineData("--demo-settings")]
    public void Allowed_invocations_pass_on_a_test_target(string extra)
    {
        Assert.Null(CliRunner.DemoArgsError(["seed-demo", "--yes-demo", extra], TargetEnv(("LIVE", "false"))));
        Assert.Null(CliRunner.DemoArgsError(["seed-demo", "--yes-demo"], TargetEnv()));
        Assert.Null(CliRunner.DemoArgsError(["seed-demo", "--yes-demo", "--reset-demo", "--demo-settings", "--edge-fx"], TargetEnv()));
    }

    [Theory]
    [InlineData("ALLOW_DEMO_SEED", "ALLOW_DEMO_SEED=true")]
    [InlineData("DB_AUTH_MODE", "DB_AUTH_MODE must be sql-password")]
    [InlineData("AUTH_MODE", "AUTH_MODE must be test-access-code")]
    public void A_missing_target_variable_is_refused(string missing, string expected)
    {
        Assert.Contains(expected, CliRunner.DemoArgsError(["seed-demo", "--yes-demo"], TargetWithout(missing)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ALLOW_DEMO_SEED", "True")]
    [InlineData("ALLOW_DEMO_SEED", "1")]
    [InlineData("ALLOW_DEMO_SEED", "false")]
    [InlineData("ALLOW_DEMO_SEED", "")]
    [InlineData("DB_AUTH_MODE", "managed-identity")]
    [InlineData("DB_AUTH_MODE", "default-azure")]
    [InlineData("AUTH_MODE", "entra")]
    [InlineData("AUTH_MODE", "")]
    public void A_wrong_target_variable_is_refused(string key, string value)
    {
        var pairs = TestTarget.Where(p => p.Item1 != key).Append((key, value)).ToArray();
        Assert.NotNull(CliRunner.DemoArgsError(["seed-demo", "--yes-demo"], Env(pairs)));
    }

    [Fact]
    public void An_empty_environment_is_refused_even_with_the_flag()
    {
        Assert.NotNull(CliRunner.DemoArgsError(["seed-demo", "--yes-demo"], Env()));
    }

    [Fact]
    public void The_usage_text_makes_no_row_count_promise_and_names_every_option()
    {
        var text = CliRunner.DemoArgsError(["seed-demo"], Env())!;
        Assert.DoesNotContain("about 600", text, StringComparison.Ordinal);
        Assert.Contains("--demo-settings", text, StringComparison.Ordinal);
        Assert.Contains("--edge-fx", text, StringComparison.Ordinal);
    }
}
