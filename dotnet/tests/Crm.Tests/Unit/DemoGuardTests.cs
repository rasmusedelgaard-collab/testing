using Crm.Web.Config;
using Microsoft.Extensions.Configuration;

namespace Crm.Tests.Unit;

/// <summary>seed-demo safety gate: refused on live/Azure hosts, needs --yes-demo, no unknown options.</summary>
public sealed class DemoGuardTests
{
    private static IConfiguration Env(params (string, string)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Item1, p => (string?)p.Item2)).Build();

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    public void Live_in_any_spelling_is_refused(string live)
    {
        var error = CliRunner.DemoArgsError(["seed-demo", "--yes-demo"], Env(("LIVE", live)));
        Assert.Contains("LIVE", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WEBSITE_INSTANCE_ID")]
    [InlineData("WEBSITE_SITE_NAME")]
    public void Azure_app_service_is_refused(string key)
    {
        Assert.Contains("Azure", CliRunner.DemoArgsError(["seed-demo", "--yes-demo"], Env((key, "abc"))), StringComparison.Ordinal);
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
    public void Allowed_invocations_pass(string extra)
    {
        Assert.Null(CliRunner.DemoArgsError(["seed-demo", "--yes-demo", extra], Env(("LIVE", "false"))));
        Assert.Null(CliRunner.DemoArgsError(["seed-demo", "--yes-demo"], Env()));
    }
}
