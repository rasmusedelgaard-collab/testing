using System.Globalization;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Pages.Entity;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages.Dashboard;

public sealed record TileVm(string Label, string Href, string Short, string Full, string Context, IReadOnlyList<string> Notes, bool Excluded, string FxCurrencies, bool Danger, bool Wrap, TrendResult? Trend = null, SparkResult? Spark = null);

public sealed record UpcomingGroup(string Title, int Total, IReadOnlyList<TimelineItem> Items, string ViewAll, string ViewAllLabel);

/// <summary>One open stage of the funnel: count, amount in the base currency (compact, full value for title/hidden text) and the bar share.</summary>
public sealed record FunnelRow(string Stage, string Href, int Count, string Short, string Full, decimal Share);

/// <summary>Dashboard (route /). All numbers come from one RunAsync (one RLS transaction): RLS filters every figure.</summary>
public sealed class IndexModel(IRlsDb rls, IRlsContextAccessor identity, TimeProvider time) : PageModel
{
    public string Greeting { get; private set; } = "";

    public string AsOf { get; private set; } = "";

    public IReadOnlyList<TileVm> Tiles { get; private set; } = [];

    public bool IsAdmin { get; private set; }

    public int UserId { get; private set; }

    public IReadOnlyList<string> OppColumns { get; private set; } = [];

    public IReadOnlyList<IReadOnlyList<Cell>> Opps { get; private set; } = [];

    public int OppTotal { get; private set; }

    public IReadOnlyList<UpcomingGroup> Upcoming { get; private set; } = [];

    public IReadOnlyList<FunnelRow> Funnel { get; private set; } = [];

    public IReadOnlyList<string> FunnelNotes { get; private set; } = [];

    public string BaseCurrency { get; private set; } = "";

    public string LongDate { get; private set; } = "";

    public string FirstName { get; private set; } = "";

    private sealed record TrendRows(IReadOnlyList<AggRow> MonthNow, IReadOnlyList<AggRow> MonthPrev, IReadOnlyList<AggRow> QuarterNow, IReadOnlyList<AggRow> QuarterPrev, IReadOnlyList<AggRow> WinPrev, IReadOnlyList<AggRow> Spark);

    private static ListQuery Q(string today, Dictionary<string, string> filters, string sort = "due") => new("", 1, 10, sort, "asc", filters, today);

    public static string GreetingFor(string tz, TimeProvider time)
    {
        var h = TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZones.Find(tz)).Hour;
        return h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
    }

    public async Task<IActionResult> OnGetAsync()
    {
        ViewData["Title"] = "Dashboard";
        ViewData["ActiveNav"] = "dashboard";
        var ctx = identity.Current;
        var data = await rls.RunAsync(async tx =>
        {
            var repo = new CrmRepo(tx, ctx);
            var lk = await repo.LookupsAsync();
            var s = lk.Settings;
            var today = TimeZones.TodayIn(s.Timezone, time).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var quarter = Periods.QuarterRange(today, s.FiscalStartMonth);
            var win = Kpi.WinRateRange(today);
            // Sequential on purpose: one transaction = one connection = one command at a time.
            var rates = await repo.FxRatesAsync(s.BaseCurrency);
            var openRows = await repo.OppAggregatesAsync(new OppAggFilter(["open"], GroupBy: ["stage"]));
            var quarterWon = await repo.OppAggregatesAsync(new OppAggFilter(["won"], From: quarter.From, To: quarter.To, GroupBy: ["month"]));
            var winRows = await repo.OppAggregatesAsync(new OppAggFilter(["won", "lost"], From: win.From, To: win.To));
            var counts = await repo.ActivityCountsAsync(today);
            var uid = ctx.UserId.ToString(CultureInfo.InvariantCulture);
            var mine = await repo.ListAsync("opportunities", Q(today, new() { ["status"] = "open", ["owner"] = uid }, "close"));
            Dictionary<string, string> Act(string due) => new() { ["owner"] = uid, ["status"] = "open", ["type"] = "actionable", ["due"] = due };
            var overdue = await repo.ListAsync("activities", Q(today, Act("overdue")));
            var dueToday = await repo.ListAsync("activities", Q(today, Act("today")));
            var week = await repo.ListAsync("activities", Q(today, Act("week")));
            var tiles = Kpi.ComputeTiles(today, s.FiscalStartMonth, lk.Stages, new Converter(s.BaseCurrency, rates), openRows, quarterWon, winRows);
            // Trends (B-6): the same aggregate with the previous-period ranges. Sequential, same transaction.
            var mtd = Trends.MonthToDate(today);
            var qtd = Trends.QuarterToDate(today, s.FiscalStartMonth);
            var prevWin = Trends.PreviousWinWindow(today);
            var sparkFrom = Periods.MonthStartPlus(today, -11);
            var trendRows = new TrendRows(
                await repo.OppAggregatesAsync(new OppAggFilter(["won"], mtd.Current.From, mtd.Current.To, GroupBy: ["month"])),
                await repo.OppAggregatesAsync(new OppAggFilter(["won"], mtd.Previous.From, mtd.Previous.To, GroupBy: ["month"])),
                await repo.OppAggregatesAsync(new OppAggFilter(["won"], qtd.Current.From, qtd.Current.To, GroupBy: ["month"])),
                await repo.OppAggregatesAsync(new OppAggFilter(["won"], qtd.Previous.From, qtd.Previous.To, GroupBy: ["month"])),
                await repo.OppAggregatesAsync(new OppAggFilter(["won", "lost"], prevWin.From, prevWin.To)),
                await repo.OppAggregatesAsync(new OppAggFilter(["won"], sparkFrom, Periods.EndAfterMonths(Periods.MonthStartPlus(today, 0), 1), GroupBy: ["month"])));
            return (lk, today, tiles, counts, mine, overdue, dueToday, week, rates, openRows, winRows, trendRows, mtd, qtd, prevWin, sparkFrom);
        }, HttpContext.RequestAborted);

        var set = data.lk.Settings;
        var f = new Formatter(set.LocaleCode, set.Timezone);
        var vms = data.tiles.Select(t => new TileVm(t.Label, t.Href, t.Value is null ? "–" : t.Kind == "money" ? NumberFormats.CompactMoney(t.Value.Value, set.BaseCurrency, set.LocaleCode) : NumberFormats.Percent(t.Value.Value / 100m, set.LocaleCode),
            t.Value is null ? "No closed deals yet" : t.Kind == "money" ? f.Money(t.Value.Value, set.BaseCurrency) : "", t.Context, t.Notes, t.ExcludedCurrencies.Count > 0, string.Join(", ", t.ExcludedCurrencies), false, false)).ToList();
        var conv = new Converter(set.BaseCurrency, data.rates);
        var curMonth = Periods.MonthOf(data.today);
        decimal Won(IReadOnlyList<AggRow> rows) => conv.ConvertRows(rows, r => (r.Currency, r.Count, r.Amount), r => r.Month ?? curMonth).Total;
        string Day(string d) => f.Date(d);
        var wrNow = Kpi.WinRate(data.winRows).Rate;
        var trends = new Dictionary<string, TrendResult?>
        {
            ["won-month"] = Trends.CompareAmounts(Won(data.trendRows.MonthNow), Won(data.trendRows.MonthPrev), "month", set.LocaleCode, data.mtd.Current, data.mtd.Previous, Day),
            ["won-quarter"] = Trends.CompareAmounts(Won(data.trendRows.QuarterNow), Won(data.trendRows.QuarterPrev), "quarter", set.LocaleCode, data.qtd.Current, data.qtd.Previous, Day),
            ["win-rate"] = Trends.ComparePoints(wrNow, Kpi.WinRate(data.trendRows.WinPrev).Rate, Kpi.WinRateRange(data.today), data.prevWin, Day),
        };
        var spark = Trends.Spark(data.trendRows.Spark, data.sparkFrom, data.today, conv, curMonth, set.LocaleCode);
        for (var i = 0; i < vms.Count; i++)
        {
            var key = data.tiles[i].Key;
            vms[i] = vms[i] with { Trend = trends.GetValueOrDefault(key), Spark = key is "won-month" or "won-quarter" ? spark : null };
        }
        // Overdue work leads when there is any; otherwise the tile shows what is due today.
        var anyOverdue = data.counts.Overdue > 0;
        vms.Add(new TileVm("Activities due", "/activities?status=open&type=actionable&due=due", anyOverdue ? $"{data.counts.Overdue} overdue" : $"{data.counts.Today} due today", "",
            anyOverdue ? $"{data.counts.Today} due today" : "Nothing overdue", [], false, "", anyOverdue, true));
        Tiles = vms;
        // The dashboard lists are "mine", so the Owner column would only repeat the user's own name.
        int OwnerIdx(string e) => EntityViews.ColumnsFor(e).ToList().FindIndex(c => c.Label == "Owner");
        IReadOnlyList<IReadOnlyList<Cell>> Rows(IReadOnlyList<Row> rs, string e) => rs.Select(r => (IReadOnlyList<Cell>)EntityViews.CellsFor(e, r, f, data.today, "/", data.lk.Stages).Where((_, i) => i != OwnerIdx(e)).ToList()).ToList();
        IReadOnlyList<TimelineItem> Items(IReadOnlyList<Row> rs) => rs.Select(r => TimelineBuilder.ActivityItem(r, f, data.today, "/")).ToList();
        var user = CrmPrincipal_Name();
        FirstName = user.Split(' ')[0] is { Length: > 0 } first ? first : "there";
        Greeting = $"{GreetingFor(set.Timezone, time)}, {FirstName}";
        AsOf = f.DateTime(time.GetUtcNow().ToString("o", CultureInfo.InvariantCulture));
        LongDate = DateOnly.ParseExact(data.today, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dddd d MMMM", CultureInfo.InvariantCulture);
        BaseCurrency = set.BaseCurrency;
        IsAdmin = ctx.Role == CrmRole.Admin;
        UserId = ctx.UserId;
        OppColumns = EntityViews.ColumnsFor("opportunities").Select(c => c.Label).Where(l => l != "Owner").ToList();
        Opps = Rows(data.mine.Rows, "opportunities");
        OppTotal = data.mine.Total;
        Upcoming =
        [
            new("Overdue", data.overdue.Total, Items(data.overdue.Rows), "/activities?status=open&type=actionable&due=overdue", "View all overdue activities"),
            new("Today", data.dueToday.Total, Items(data.dueToday.Rows), "/activities?status=open&type=actionable&due=today", "View all activities due today"),
            new("Next 7 days", data.counts.Next7, Items(data.week.Rows), "/activities?status=open&type=actionable&due=week", "View all activities in the next 7 days"),
        ];
        // Open pipeline by stage: the aggregate the tiles already use (no new query), converted as the tiles are.
        var openStages = data.lk.Stages.Where(st => st.Type == "open").OrderBy(st => st.SortOrder).ToList();
        var perStage = openStages.Select(st => (Stage: st, Conv: conv.ConvertRows(data.openRows.Where(r => r.StageId == st.Id), r => (r.Currency, r.Count, r.Amount), _ => curMonth), Count: data.openRows.Where(r => r.StageId == st.Id).Sum(r => r.Count))).ToList();
        var maxAmount = perStage.Count == 0 ? 0m : perStage.Max(x => x.Conv.Total);
        Funnel = perStage.Select(x => new FunnelRow(x.Stage.Name, $"/opportunities?status=open&stage={x.Stage.Id}", x.Count,
            NumberFormats.CompactMoney(x.Conv.Total, set.BaseCurrency, set.LocaleCode), f.Money(x.Conv.Total, set.BaseCurrency), maxAmount == 0 ? 0 : x.Conv.Total / maxAmount * 100)).ToList();
        FunnelNotes = vms.Count > 0 ? vms[0].Notes : [];
        return Page();
    }

    private string CrmPrincipal_Name() => User.FindFirst("name")?.Value ?? "";
}
