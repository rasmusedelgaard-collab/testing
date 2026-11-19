using Crm.Web.Infrastructure;
using Crm.Web.Services;

namespace Crm.Tests.Unit;

public class SettingsConfigUnitTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("2147483647", true)]
    [InlineData("2147483648", false)]
    [InlineData("0", false)]
    [InlineData("005", false)]
    [InlineData("+5", false)]
    [InlineData(" 5", false)]
    [InlineData("5 ", false)]
    [InlineData("-5", false)]
    [InlineData("1e3", false)]
    [InlineData("٣", false)]
    [InlineData("", false)]
    [InlineData("99999999999", false)]
    public void Id_constraint_accepts_only_canonical_positive_ints(string s, bool ok) => Assert.Equal(ok, CrmIdConstraint.IsCanonicalId(s));

    private static readonly string[] Zones = ["UTC", "Europe/Copenhagen"];

    [Fact]
    public void Locale_validation_always_enables_the_base_and_drops_unknown_codes()
    {
        var (v, e) = SettingsValidators.ValidateLocale(" EUR ", ["USD", "ZZZ", "USD"], "en-GB", "UTC", "7", "12", Zones);
        Assert.Empty(e);
        Assert.Equal(["EUR", "USD"], v.Enabled);
        var (_, bad) = SettingsValidators.ValidateLocale("", null, "xx", "Mars", "0", "13", Zones);
        Assert.Equal(["base", "locale", "timezone", "firstDay", "fiscalStart"], bad.Keys.ToArray());
        Assert.Equal("Choose a month.", SettingsValidators.ValidateLocale("DKK", null, "da-DK", "UTC", "1", "+1", Zones).Errors["fiscalStart"]);
    }

    [Theory]
    [InlineData("da-DK", "7,459123", true, "Enter a rate, for example 7,4590.")]
    [InlineData("da-DK", "7.459", false, "Enter a rate, for example 7,4590.")]
    [InlineData("da-DK", "7,4590001", false, "Enter a rate, for example 7,4590.")]
    [InlineData("en-GB", "7.459", true, "Enter a rate, for example 7.4590.")]
    [InlineData("en-GB", "7,459", false, "Enter a rate, for example 7.4590.")]
    [InlineData("en-GB", "0", false, "Rate must be more than 0.")]
    [InlineData("en-GB", "-2", false, "Rate must be more than 0.")]
    [InlineData("en-GB", "", false, "Enter a rate, for example 7.4590.")]
    public void Fx_rate_parsing_is_strict_and_locale_bound(string locale, string rate, bool ok, string message)
    {
        var (_, _, value, errors) = SettingsValidators.ValidateFx("EUR", "2026-03", rate, "DKK", ["DKK", "EUR"], locale);
        Assert.Equal(ok, errors.Count == 0);
        if (!ok) { Assert.Equal(message, errors["rate"]); }
        else { Assert.True(value > 0); }
    }

    [Fact]
    public void Fx_validation_rules_for_currency_and_month()
    {
        Assert.Equal("Choose a currency.", SettingsValidators.ValidateFx("DKK", "2026-03", "1", "DKK", ["DKK", "EUR"], "en-GB").Errors["currency"]);
        Assert.Equal("Choose a currency.", SettingsValidators.ValidateFx("SEK", "2026-03", "1", "DKK", ["DKK", "EUR"], "en-GB").Errors["currency"]);
        Assert.Equal("Choose a month.", SettingsValidators.ValidateFx("EUR", "1999-12", "1", "DKK", ["DKK", "EUR"], "en-GB").Errors["month"]);
        Assert.Equal("Choose a month.", SettingsValidators.ValidateFx("EUR", "2026-3", "1", "DKK", ["DKK", "EUR"], "en-GB").Errors["month"]);
        Assert.Empty(SettingsValidators.ValidateFx("eur", "2026-03", "1", "DKK", ["DKK", "EUR"], "en-GB").Errors);
    }

    private static SettingsValidators.StageForm S(string id, string name, string prob, string type) => new(id, name, prob, type);

    [Fact]
    public void Stages_force_won_100_and_lost_0_and_need_exactly_one_of_each()
    {
        var (rows, errors, summary) = SettingsValidators.ValidateStages([S("1", "A", "10", "open"), S("2", "W", "5", "won"), S("3", "L", "77", "lost")]);
        Assert.Empty(errors);
        Assert.Empty(summary);
        Assert.Equal([10, 100, 0], rows.Select(r => r.Probability).ToArray());
        var (_, e2, s2) = SettingsValidators.ValidateStages([S("", "A", "x", "open"), S("", "a", "10", "won"), S("", "", "10", "won")]);
        Assert.Equal("Probability must be between 0 and 100.", e2["prob0"]);
        Assert.Equal("Each stage needs a different name.", e2["name1"]);
        Assert.Equal("Enter a stage name.", e2["name2"]);
        Assert.Equal(["You need exactly one Won stage and one Lost stage."], s2);
        Assert.Equal("You need at least one open stage.", SettingsValidators.ValidateStages([S("", "W", "", "won"), S("", "L", "", "lost")]).Summary.Single());
        Assert.Equal("Stage name must be 60 characters or fewer.", SettingsValidators.ValidateStages([S("", new string('x', 61), "1", "open")]).Errors["name0"]);
    }

    [Fact]
    public void Stage_ids_that_overflow_become_unknown_ids_not_new_rows()
    {
        var (rows, _, _) = SettingsValidators.ValidateStages([S("9999999999", "A", "1", "open"), S("abc", "B", "1", "open")]);
        Assert.Equal(int.MaxValue, rows[0].Id);
        Assert.Null(rows[1].Id);
    }

    [Theory]
    [InlineData("", "Enter a name.")]
    [InlineData("   ", "Enter a name.")]
    public void Label_validation(string raw, string message) => Assert.Equal(message, SettingsValidators.ValidateLabel(raw));

    [Fact]
    public void Label_length_limit_is_80() { Assert.Null(SettingsValidators.ValidateLabel(new string('x', 80))); Assert.NotNull(SettingsValidators.ValidateLabel(new string('x', 81))); }

    [Fact]
    public void Listed_time_zones_include_utc_and_copenhagen_and_resolve()
    {
        Assert.Contains("UTC", TimeZones.Listed);
        Assert.Contains("Europe/Copenhagen", TimeZones.Listed);
        Assert.All(TimeZones.Listed, z => Assert.True(TimeZones.IsValid(z), z));
    }
}

public class LikeEscapeTests
{
    [Theory]
    [InlineData("a%b", "a\\%b")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("a[b", "a\\[b")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("end\\", "end\\\\")]
    public void Wildcards_and_the_escape_character_are_escaped_in_pairs(string input, string expected) =>
        Assert.Equal(expected, Crm.Web.Data.CrmRepo.EscapeLike(input));

    [Fact]
    public void The_worst_case_pattern_fits_the_declared_parameter_size_and_never_ends_in_a_lone_escape()
    {
        var pattern = $"%{Crm.Web.Data.CrmRepo.EscapeLike(new string('%', 100))}%";
        Assert.Equal(202, pattern.Length);
        Assert.True(pattern.Length <= 4000);
        var run = pattern.Reverse().TakeWhile(c => c == '\\').Count();
        Assert.Equal(0, run % 2);
    }
}

public class StageSafetyTests
{
    [Fact]
    public void Stage_names_cannot_start_with_the_placeholder_character() =>
        Assert.Equal("Stage name can't start with ~.", SettingsValidators.ValidateStages([new("", "~1", "10", "open")]).Errors["name0"]);

    [Fact]
    public void The_stage_version_changes_with_any_id_or_modification_time()
    {
        Crm.Web.Data.StageUsageRow S(int id, string at) => new(id, "n", id, 10, "open", 0, at);
        var a = Crm.Web.Data.AdminRepo.StageVersion([S(1, "2026-01-01T00:00:00.000"), S(2, "2026-01-01T00:00:00.000")]);
        Assert.Equal(a, Crm.Web.Data.AdminRepo.StageVersion([S(2, "2026-01-01T00:00:00.000"), S(1, "2026-01-01T00:00:00.000")])); // order independent
        Assert.NotEqual(a, Crm.Web.Data.AdminRepo.StageVersion([S(1, "2026-01-01T00:00:00.000"), S(2, "2026-01-01T00:00:00.001")]));
        Assert.NotEqual(a, Crm.Web.Data.AdminRepo.StageVersion([S(1, "2026-01-01T00:00:00.000")]));
        Assert.NotEqual(a, Crm.Web.Data.AdminRepo.StageVersion([S(1, "2026-01-01T00:00:00.000"), S(3, "2026-01-01T00:00:00.000")]));
    }
}
