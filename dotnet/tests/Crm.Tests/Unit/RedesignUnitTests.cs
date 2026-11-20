using Crm.Web.Domain;
using Crm.Web.Infrastructure;
using Crm.Web.Pages;
using Crm.Web.Pages.Entity;
using Crm.Web.Services;

namespace Crm.Tests.Unit;

public sealed class RedesignUnitTests
{
    private static string Day(string d) => d;

    [Fact]
    public void Accent_contrast_is_checked_against_white_and_the_light_blue_tint()
    {
        Assert.True(Color.AccentContrast("#1A66CC") >= 4.5);
        Assert.True(Color.AccentContrast("#00456C") >= 4.5);
        Assert.True(Color.AccentContrast("#0B5D57") >= 4.5);
        Assert.True(Color.AccentContrast("#1E6FD9") < 4.5); // passes on white (4.85), fails on the #EAF2FD tint (4.3)
        Assert.True(Color.ContrastWithWhite("#1E6FD9") >= 4.5);
    }

    [Fact]
    public void Legacy_default_and_too_light_accents_count_as_not_set()
    {
        Assert.Null(Color.EffectiveAccent("#1D4ED8"));
        Assert.Null(Color.EffectiveAccent("#1A66CC"));
        Assert.Equal("#00456C", Color.EffectiveAccent("#00456c"));
        Assert.Null(Color.EffectiveAccent("#FFFF00"));
        Assert.Null(Color.EffectiveAccent("red"));
        Assert.Equal("#0B5D57", Color.EffectiveAccent("#0b5d57"));
    }

    [Fact]
    public void Theme_css_overrides_the_action_colour_for_Light_only()
    {
        var css = BrandingEndpoints.ThemeCss("#0B5D57");
        Assert.Contains("@media (prefers-color-scheme: light) { :root:not([data-theme=\"dark\"]) { --action: #0B5D57; --action-hover: #", css, StringComparison.Ordinal);
        Assert.Contains(":root[data-theme=\"light\"] { --action: #0B5D57;", css, StringComparison.Ordinal);
        // A dark page never gets the accent: every --action rule is scoped to light.
        foreach (var line in css.Split('\n').Where(l => l.Contains("--action:", StringComparison.Ordinal)))
        {
            Assert.True(line.Contains("prefers-color-scheme: light", StringComparison.Ordinal) || line.StartsWith(":root[data-theme=\"light\"]", StringComparison.Ordinal), line);
        }
        Assert.DoesNotContain("--action:", BrandingEndpoints.ThemeCss("#1D4ED8"), StringComparison.Ordinal);
    }

    [Fact]
    public void Month_to_date_compares_the_same_number_of_days()
    {
        var (cur, prev) = Trends.MonthToDate("2026-11-19");
        Assert.Equal(new DateRange("2026-11-01", "2026-11-19"), cur);
        Assert.Equal(new DateRange("2026-10-01", "2026-10-19"), prev);
        var (_, febPrev) = Trends.MonthToDate("2026-03-31");
        Assert.Equal(new DateRange("2026-02-01", "2026-02-28"), febPrev); // clamped to the end of the shorter month
    }

    [Fact]
    public void Quarter_to_date_and_the_previous_win_window()
    {
        var (cur, prev) = Trends.QuarterToDate("2026-11-19", 1);
        Assert.Equal(new DateRange("2026-10-01", "2026-11-19"), cur);
        Assert.Equal(new DateRange("2026-07-01", "2026-08-19"), prev);
        var win = Kpi.WinRateRange("2026-11-19");
        var before = Trends.PreviousWinWindow("2026-11-19");
        Assert.Equal(Periods.AddDays(win.From, -1), before.To);
        Assert.Equal(Kpi.WinRateDays, Periods.DaysBetween(before.From, before.To) + 1);
    }

    [Theory]
    [InlineData(112, 100, "up", "Up 12% vs same days last month")]
    [InlineData(92, 100, "down", "Down 8% vs same days last month")]
    [InlineData(100, 100, "flat", "No change vs same days last month")]
    [InlineData(50, 0, "up", "None in the same days last month")]
    [InlineData(0, 0, "flat", "No change vs same days last month")]
    public void Amount_trends_use_the_exact_copy(int current, int previous, string direction, string text)
    {
        var t = Trends.CompareAmounts(current, previous, "month", "en-GB", new DateRange("2026-11-01", "2026-11-19"), new DateRange("2026-10-01", "2026-10-19"), Day)!;
        Assert.Equal(direction, t.Direction);
        Assert.Equal(text, t.Text);
        Assert.Contains("compared with 2026-10-01–2026-10-19", t.Range, StringComparison.Ordinal);
    }

    [Fact]
    public void Quarter_wording_and_danish_percent_format()
    {
        var t = Trends.CompareAmounts(92, 100, "quarter", "da-DK", new DateRange("a", "b"), new DateRange("c", "d"), Day)!;
        Assert.Equal("Down 8 % vs same days last quarter", t.Text.Replace("\u00a0", " ", StringComparison.Ordinal));
    }

    [Fact]
    public void Win_rate_trend_is_in_points_and_needs_both_windows()
    {
        var r = new DateRange("a", "b");
        Assert.Equal("Up 4 pts vs previous 90 days", Trends.ComparePoints(0.38m, 0.34m, r, r, Day)!.Text);
        Assert.Equal("Down 3 pts vs previous 90 days", Trends.ComparePoints(0.31m, 0.34m, r, r, Day)!.Text);
        Assert.Equal("No change vs previous 90 days", Trends.ComparePoints(0.34m, 0.34m, r, r, Day)!.Text);
        Assert.Null(Trends.ComparePoints(null, 0.34m, r, r, Day));
    }

    [Fact]
    public void Sparkline_has_twelve_points_on_a_zero_baseline_and_a_text_alternative()
    {
        var conv = new Converter("DKK", []);
        var rows = new List<AggRow>
        {
            new("won", null, null, null, null, "2026-06", null, "DKK", 2, 3_100_000m),
            new("won", null, null, null, null, "2026-11", null, "DKK", 1, 1_000_000m),
        };
        var s = Trends.Spark(rows, "2025-12-01", "2026-11-19", conv, "2026-11", "en-GB");
        Assert.Equal(12, s.Points.Split(' ').Length);
        Assert.Equal("Last 12 months: highest in June 2026, DKK 3.1M.", s.Text);
        Assert.Equal("Last 12 months: nothing won.", Trends.Spark([], "2025-12-01", "2026-11-19", conv, "2026-11", "en-GB").Text);
    }

    private static Row Act(int id, string type, string subject, string due, bool done, string created = "2026-11-01T08:00:00.000Z", string time = "") =>
        new(new Dictionary<string, object?> { ["Id"] = id, ["ActivityType"] = type, ["Subject"] = subject, ["DueDate"] = due.Length == 0 ? null : due, ["DueTime"] = time.Length == 0 ? null : time, ["IsDone"] = done, ["Description"] = "", ["OwnerName"] = "Anna Holm", ["CreatedAt"] = created, ["OpportunityId"] = 7, ["OpportunityName"] = "Blade root seals" });

    [Fact]
    public void Timeline_groups_overdue_upcoming_then_months_newest_first()
    {
        var f = new Formatter("en-GB", "Europe/Copenhagen");
        var acts = new[]
        {
            Act(1, "task", "Send quote", "2026-11-10", false),
            Act(2, "meeting", "Review", "2026-11-26", false),
            Act(3, "call", "Intro", "2026-10-02", true),
            Act(4, "note", "Memo", "", false, "2026-11-05T09:00:00.000Z"),
        };
        var history = new[] { new HistoryRow("2026-11-18T09:12:00.000Z", "Mette Holm", "U", "StageId", "Needs analysis", "Proposal"), new HistoryRow("2026-10-01T09:00:00.000Z", "Anna Holm", "C", null, null, null) };
        var groups = TimelineBuilder.Build(acts, history, f, "2026-11-19", "/opportunities/7", "opportunities", 7);
        Assert.Equal(["Overdue", "Upcoming", "November 2026", "October 2026"], groups.Select(g => g.Title));
        Assert.Equal("Task: Send quote", groups[0].Items[0].Title);
        Assert.True(groups[0].Items[0].Overdue);
        Assert.Equal("9 days overdue", groups[0].Items[0].Hint);
        Assert.Equal("in 7 days", groups[1].Items[0].Hint);
        Assert.Equal(["Note: Memo", "Stage changed from Needs analysis to Proposal"], groups[2].Items.Select(i => i.Title).Reverse());
        Assert.Equal("Created", groups[3].Items.Last().Title);
        Assert.Null(groups[2].Items.First(i => i.Title.StartsWith("Note", StringComparison.Ordinal)).Action); // notes have no mark-done
        Assert.Null(groups[0].Items[0].RelatedLabel); // the page's own record is not repeated
        Assert.Equal(6, TimelineBuilder.Count(groups));
    }

    [Theory]
    [InlineData("2026-11-19", "2026-11-19", true, "today")]
    [InlineData("2026-11-20", "2026-11-19", true, "tomorrow")]
    [InlineData("2026-11-18", "2026-11-19", true, "1 day overdue")]
    [InlineData("2026-11-17", "2026-11-19", false, "2 days ago")]
    public void Relative_days(string due, string today, bool open, string expected) => Assert.Equal(expected, TimelineBuilder.RelativeDays(due, today, open));

    [Fact]
    public void Form_sections_group_and_pair_fields_per_entity()
    {
        var fields = new[] { "Name", "AccountId", "PrimaryContactId", "StageId", "Amount", "CurrencyCode", "CloseDate", "LeadSourceValueId", "LostReasonValueId", "OwnerId" }
            .Select(n => new FormField(n, n, "text", "")).ToList();
        var sections = EntityViews.Sections("opportunities", fields);
        Assert.Equal(["Basics", "Value and timing", "Source and ownership"], sections.Select(s => s.Title));
        Assert.Equal(["Amount", "CurrencyCode"], sections[1].Rows[1].Select(x => x.Name)); // the pair shares a line
        Assert.Equal(fields.Count, sections.Sum(s => s.Rows.Sum(r => r.Count)));
        var act = EntityViews.Sections("activities", new[] { "ActivityType", "Subject", "AccountId", "ContactId", "OpportunityId", "DueDate", "DueTime", "IsDone", "Description", "OwnerId" }.Select(n => new FormField(n, n, "text", "")).ToList());
        Assert.True(act[1].Fieldset);
        Assert.Equal("Related to (link at least one)", act[1].Title);
    }

    [Fact]
    public void Icon_ids_are_never_injected_unsafely()
    {
        Assert.Contains("#i-call\"", Ui.Icon("call").ToHtml(), StringComparison.Ordinal);
        Assert.Contains("#i-info\"", Ui.Ring("\"><script>").ToHtml(), StringComparison.Ordinal);
        Assert.DoesNotContain("<script", Ui.Icon("x\" onload=\"y").ToHtml(), StringComparison.Ordinal);
    }

    [Fact]
    public void Stage_chips_and_segments_render_text_first()
    {
        Assert.Contains("class=\"chip won\">Closed won", Ui.StageChip("Closed won", "won", 0, 4).ToHtml(), StringComparison.Ordinal);
        var open = Ui.StageChip("Proposal", "open", 3, 4).ToHtml();
        Assert.Contains("Proposal", open, StringComparison.Ordinal);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(open, "pip on").Count);
        Assert.Contains("segment wind", Ui.SegmentChip("wind").ToHtml(), StringComparison.Ordinal);
        Assert.Equal("", Ui.SegmentChip("other").ToHtml());
        Assert.Equal("AH", Ui.Initials("Anna Holm"));
        Assert.DoesNotContain("<script", Ui.Owner("<script>x</script>").ToHtml(), StringComparison.Ordinal);
    }

    [Fact]
    public void Active_filter_chips_link_to_the_list_without_that_filter()
    {
        var def = EntityDefinitions.All["opportunities"];
        var q = new ListQuery("blade", 2, 25, "close", "asc", new Dictionary<string, string> { ["status"] = "open", ["stage"] = "3" }, "2026-11-19");
        var fields = new[] { new FilterField("stage", "Stage", [new SelectOption("", "All"), new SelectOption("3", "Proposal")], "3"), new FilterField("status", "Status", [new SelectOption("open", "Open")], "open") };
        var chips = EntityViews.ActiveFilters(def, q, fields);
        Assert.Equal(2, chips.Count); // search text and stage; status=open is the default
        Assert.Equal("Search: blade", chips[0].Label);
        Assert.Equal("Stage: Proposal", chips[1].Label);
        Assert.DoesNotContain("stage=", chips[1].Href, StringComparison.Ordinal);
        Assert.Contains("page=1", chips[1].Href, StringComparison.Ordinal);
    }
}
