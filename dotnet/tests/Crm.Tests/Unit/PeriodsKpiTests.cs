using Crm.Web.Domain;
using Crm.Web.Services;

namespace Crm.Tests.Unit;

/// <summary>Ports of the periods and KPI parts of kpi.test.ts (decimal money; expected values documented where doubles differed).</summary>
public class PeriodsKpiTests
{
    [Fact]
    public void Quarters_follow_the_fiscal_start_month()
    {
        Assert.Equal(new DateRange("2026-10-01", "2026-12-31"), Periods.QuarterRange("2026-11-18", 1));
        Assert.Equal(new DateRange("2026-01-01", "2026-03-31"), Periods.QuarterRange("2026-03-31", 1));
        Assert.Equal(new DateRange("2026-04-01", "2026-06-30"), Periods.QuarterRange("2026-04-01", 1));
        Assert.Equal(new DateRange("2026-04-01", "2026-06-30"), Periods.QuarterRange("2026-04-01", 4));
        Assert.Equal(new DateRange("2026-01-01", "2026-03-31"), Periods.QuarterRange("2026-02-10", 4));
        Assert.Equal(new DateRange("2026-10-01", "2026-12-31"), Periods.QuarterRange("2026-12-31", 4));
        Assert.Equal(new DateRange("2025-04-01", "2026-03-31"), Periods.FiscalYearRange("2026-02-10", 4));
        Assert.Equal(new DateRange("2026-04-01", "2027-03-31"), Periods.FiscalYearRange("2026-04-01", 4));
        Assert.Equal(new DateRange("2026-11-01", "2027-01-31"), Periods.QuarterRange("2026-12-15", 11));
        Assert.Equal(new DateRange("2026-11-01", "2027-01-31"), Periods.QuarterRange("2027-01-31", 11));
        Assert.Equal(new DateRange("2027-02-01", "2027-04-30"), Periods.QuarterRange("2027-02-01", 11));
    }

    [Fact]
    public void Leap_years_and_month_ends()
    {
        Assert.Equal(new DateRange("2028-01-01", "2028-03-31"), Periods.QuarterRange("2028-02-29", 1));
        Assert.Equal("2028-02-29", Periods.AddDays("2028-02-28", 1));
        Assert.Equal("2027-03-01", Periods.AddDays("2027-02-28", 1));
        Assert.Equal("2027-01-01", Periods.AddDays("2026-12-31", 1));
        Assert.Equal(new DateRange("2026-08-21", "2026-11-18"), Kpi.WinRateRange("2026-11-18"));
    }

    [Fact]
    public void Presets_and_custom_validation_with_exact_copy()
    {
        PeriodResult R(string p, string from = "", string to = "", string shown = "", string today = "2026-11-18", int fy = 1) => Periods.Resolve(p, from, to, shown, today, fy);
        Assert.Equal(new DateRange("2026-11-01", "2026-11-30"), R("this-month").Range);
        Assert.Equal(new DateRange("2025-12-01", "2025-12-31"), R("last-month", today: "2026-01-10").Range);
        Assert.Equal(new DateRange("2025-10-01", "2025-12-31"), R("last-quarter", today: "2026-01-10").Range);
        Assert.Equal(new DateRange("2025-12-01", "2026-11-30"), R("last-12-months").Range);
        Assert.Equal(new DateRange("2026-04-01", "2027-03-31"), R("this-year", fy: 4).Range);
        Assert.Equal("this-quarter", R("nonsense").Key);
        Assert.Equal(new DateRange("2026-10-01", "2026-12-31"), R("").Range);
        Assert.True(R("custom", "2026-01-01", "2026-02-01").Ok);
        Assert.Equal("End date must be on or after start date.", R("custom", "2026-02-01", "2026-01-01").Error);
        Assert.Equal("Choose a range of 5 years or less.", R("custom", "2020-01-01", "2026-01-02").Error);
        Assert.True(R("custom", "2021-01-01", "2026-01-01").Ok);
        Assert.Equal("Enter a real date, for example 31.12.2026.", R("custom", "x", "").Error);
        // editing the pre-filled dates of an unchanged preset means custom; a changed preset ignores the stale dates
        Assert.Equal("custom", R("this-quarter", "2026-10-02", "2026-12-31", shown: "this-quarter").Key);
        Assert.Equal("this-month", R("this-month", "2026-10-01", "2026-12-31", shown: "this-quarter").Key);
    }

    [Fact]
    public void Months_between_lists_every_month_and_is_capped()
    {
        Assert.Equal(["2026-11", "2026-12", "2027-01", "2027-02"], Periods.MonthsBetween("2026-11-18", "2027-02-03"));
        Assert.Equal(12, Periods.MonthsBetween("2000-01-01", "2026-01-01", 12).Count);
    }

    private static AggRow Row(string type = "open", int? stage = null, string cur = "DKK", int n = 1, decimal amt = 100m, string? month = null, int? owner = null, string? ownerName = null) =>
        new(type, stage, owner, ownerName, null, month, null, cur, n, amt);

    private static readonly Stage[] Stages = [new(1, "Qual", 1, 10, "open"), new(3, "Prop", 3, 50, "open"), new(5, "Won", 5, 100, "won"), new(6, "Lost", 6, 0, "lost")];
    private static readonly Converter Conv = new("DKK", [new("EUR", "2026-11", 7.5m)]);

    [Fact]
    public void Weighted_pipeline_is_amount_times_probability_at_the_current_month()
    {
        var rows = new[] { Row(stage: 1, amt: 1000m), Row(stage: 3, cur: "EUR", amt: 100m), Row(stage: 3, cur: "SEK", amt: 5m) };
        var p = Kpi.PipelineTotals(rows, Stages, Conv, "2026-11");
        Assert.Equal(1750m, p.Total.Total);
        Assert.Equal(100m + 375m, p.Weighted.Total);
        Assert.Equal(3, p.Count);
        Assert.Equal([("SEK", 1)], p.Total.Excluded);
    }

    [Fact]
    public void Win_rate_is_null_without_closed_deals_never_a_division_by_zero()
    {
        Assert.Null(Kpi.WinRate([]).Rate);
        Assert.Null(Kpi.WinRate([Row("open")]).Rate);
        var w = Kpi.WinRate([Row("won", n: 3), Row("lost", n: 1)]);
        Assert.Equal((3, 1, 0.75m), (w.Won, w.Lost, w.Rate));
    }

    [Fact]
    public void Tiles_have_the_documented_keys_labels_contexts_and_hrefs()
    {
        var won = new[] { Row("won", month: "2026-11", amt: 500m, n: 2), Row("won", month: "2026-10", amt: 100m) };
        var tiles = Kpi.ComputeTiles("2026-11-18", 1, Stages, Conv, [Row(stage: 1, amt: 100m)], won, [Row("won", n: 1), Row("lost", n: 1)]);
        Assert.Equal(["open", "weighted", "won-month", "won-quarter", "win-rate"], tiles.Select(t => t.Key));
        Assert.Equal("1 opp", tiles[0].Context);
        Assert.Equal("2 deals", tiles[2].Context);
        Assert.Equal(500m, tiles[2].Value);
        Assert.Equal(600m, tiles[3].Value);
        Assert.Equal("/opportunities?status=won&from=2026-11-01&to=2026-11-30", tiles[2].Href);
        Assert.Equal("/opportunities?status=won&from=2026-10-01&to=2026-12-31", tiles[3].Href);
        Assert.Equal(50m, tiles[4].Value);
        Assert.Equal("1 of 2", tiles[4].Context);
        Assert.Equal("/opportunities?status=closed&from=2026-08-21&to=2026-11-18", tiles[4].Href);
        Assert.Equal("No closed deals yet", Kpi.ComputeTiles("2026-11-18", 1, Stages, Conv, [], [], [])[4].Context);
    }

    [Fact]
    public void Compact_money_and_percent_are_pinned()
    {
        Assert.Equal("DKK 120k", NumberFormats.CompactMoney(120000m, "DKK", "en-GB"));
        Assert.Equal("DKK 1,2m", NumberFormats.CompactMoney(1_234_000m, "DKK", "da-DK"));
        Assert.Equal("DKK 950", NumberFormats.CompactMoney(950m, "DKK", "en-GB"));
        Assert.Equal("EUR 2.5bn", NumberFormats.CompactMoney(2_500_000_000m, "EUR", "en-US"));
        Assert.Equal("50%", NumberFormats.Percent(0.5m, "en-GB"));
        Assert.Equal("66,7 %", NumberFormats.Percent(2m / 3m, "da-DK", 1));
        Assert.Equal("Nov 2026", NumberFormats.MonthLabel("2026-11", "en-GB"));
        Assert.Equal("maj 2026", NumberFormats.MonthLabel("2026-05", "da-DK"));
    }

    [Fact]
    public void Charts_have_takeaways_tables_and_empty_states()
    {
        var c = new FormatCtx(new Formatter("en-GB", "UTC"), "en-GB", "DKK");
        var empty = Charts.PipelineByStage([], Stages, Conv, "2026-11", c);
        Assert.True(empty.Empty);
        Assert.Equal(Charts.EmptyText, empty.Takeaway);
        var rows = new[] { Row(stage: 1, amt: 1000m), Row(stage: 3, amt: 5000m, n: 2), Row(stage: 3, cur: "SEK", amt: 1m) };
        var p = Charts.PipelineByStage(rows, Stages, Conv, "2026-11", c);
        Assert.Equal("Prop holds the most value: DKK 5k", p.Takeaway);
        Assert.Contains("1 opportunity in SEK not included: no FX rate", p.Notes);
        Assert.Equal(["Stage", "Opportunities", "Amount (DKK)"], p.TableColumns);
        var won = Charts.WonRevenueByMonth([Row("won", month: "2026-10", amt: 100m), Row("won", month: "2026-11", amt: 300m)], new DateRange("2026-10-01", "2026-12-31"), Conv, c);
        Assert.Equal("Best month: Nov 2026 with DKK 300", won.Takeaway);
        Assert.Equal(3, won.Labels.Count);
        var wr = Charts.WinRateByOwner([Row("won", owner: 1, ownerName: "Ann", n: 3), Row("lost", owner: 1, ownerName: "Ann"), Row("won", owner: 2, ownerName: "Bo")], c);
        Assert.Equal("Bo has the highest win rate: 100%", wr.Takeaway);
        Assert.Equal([100m, 75m], wr.Data);
        var act = Charts.ActivitiesByType([("call", 2), ("task", 5)], c);
        Assert.Equal("Task is the most common type: 5 of 7", act.Takeaway);
        Assert.Equal(["Call", "Meeting", "Task", "Note"], act.Labels);
        var ls = Charts.OppsByLeadSource([new AggRow("open", null, null, null, null, null, null, "DKK", 2, 1m), new AggRow("open", null, null, null, "Web", null, null, "DKK", 1, 1m)], c);
        Assert.Equal("(none) brings the most opportunities: 2", ls.Takeaway);
    }
}
