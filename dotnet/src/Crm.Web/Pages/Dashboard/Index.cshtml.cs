using System.Globalization;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Pages.Entity;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages.Dashboard;

public sealed record TileVm(string Label, string Href, string Short, string Full, string Context, IReadOnlyList<string> Notes, bool Excluded, string FxCurrencies, bool Danger, bool Wrap);

public sealed record UpcomingGroup(string Title, int Total, IReadOnlyList<IReadOnlyList<Cell>> Rows, string ViewAll);

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

    public IReadOnlyList<string> ActColumns { get; private set; } = [];

    public IReadOnlyList<UpcomingGroup> Upcoming { get; private set; } = [];

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
            return (lk, today, tiles, counts, mine, overdue, dueToday, week);
        }, HttpContext.RequestAborted);

        var set = data.lk.Settings;
        var f = new Formatter(set.LocaleCode, set.Timezone);
        var vms = data.tiles.Select(t => new TileVm(t.Label, t.Href, t.Value is null ? "–" : t.Kind == "money" ? NumberFormats.CompactMoney(t.Value.Value, set.BaseCurrency, set.LocaleCode) : NumberFormats.Percent(t.Value.Value / 100m, set.LocaleCode),
            t.Value is null ? "No closed deals yet" : t.Kind == "money" ? f.Money(t.Value.Value, set.BaseCurrency) : "", t.Context, t.Notes, t.ExcludedCurrencies.Count > 0, string.Join(", ", t.ExcludedCurrencies), false, false)).ToList();
        // Overdue work leads when there is any; otherwise the tile shows what is due today.
        var anyOverdue = data.counts.Overdue > 0;
        vms.Add(new TileVm("Activities due", "/activities?status=open&type=actionable&due=due", anyOverdue ? $"{data.counts.Overdue} overdue" : $"{data.counts.Today} due today", "",
            anyOverdue ? $"{data.counts.Today} due today" : "Nothing overdue", [], false, "", anyOverdue, true));
        Tiles = vms;
        // The dashboard lists are "mine", so the Owner column would only repeat the user's own name.
        int OwnerIdx(string e) => EntityViews.ColumnsFor(e).ToList().FindIndex(c => c.Label == "Owner");
        IReadOnlyList<IReadOnlyList<Cell>> Rows(IReadOnlyList<Row> rs, string e) => rs.Select(r => (IReadOnlyList<Cell>)EntityViews.CellsFor(e, r, f, data.today, "/").Where((_, i) => i != OwnerIdx(e)).ToList()).ToList();
        var user = CrmPrincipal_Name();
        Greeting = $"{GreetingFor(set.Timezone, time)}, {(user.Split(' ')[0] is { Length: > 0 } first ? first : "there")}";
        AsOf = f.DateTime(time.GetUtcNow().ToString("o", CultureInfo.InvariantCulture));
        IsAdmin = ctx.Role == CrmRole.Admin;
        UserId = ctx.UserId;
        OppColumns = EntityViews.ColumnsFor("opportunities").Select(c => c.Label).Where(l => l != "Owner").ToList();
        Opps = Rows(data.mine.Rows, "opportunities");
        OppTotal = data.mine.Total;
        ActColumns = EntityViews.ColumnsFor("activities").Select(c => c.Label).Where(l => l != "Owner").ToList();
        Upcoming =
        [
            new("Overdue", data.overdue.Total, Rows(data.overdue.Rows, "activities"), "/activities?status=open&type=actionable&due=overdue"),
            new("Today", data.dueToday.Total, Rows(data.dueToday.Rows, "activities"), "/activities?status=open&type=actionable&due=today"),
            new("Next 7 days", data.counts.Next7, Rows(data.week.Rows, "activities"), "/activities?status=open&type=actionable&due=week"),
        ];
        return Page();
    }

    private string CrmPrincipal_Name() => User.FindFirst("name")?.Value ?? "";
}
