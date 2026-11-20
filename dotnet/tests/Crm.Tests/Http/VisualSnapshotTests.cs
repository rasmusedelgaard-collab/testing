using System.Globalization;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Pages;
using Crm.Web.Pages.Dashboard;
using Crm.Web.Pages.Entity;
using Crm.Web.Pages.Pipeline;
using Crm.Web.Services;
using Crm.Web.Services.Demo;

namespace Crm.Tests.Http;

/// <summary>
/// Populated pages for design review: the in-memory page models are filled from the demo generator (the same data a demo database gets),
/// so lists, the board and the timeline look like a live system. Run with CRM_RENDER_DUMP=&lt;dir&gt; to get the HTML for screenshots
/// (docs/ux/screenshots/v2/render.sh). Without it the tests are a render smoke test with a few structural checks.
/// </summary>
public sealed class VisualSnapshotTests : IDisposable
{
    private const string Today = "2026-11-19";
    private static readonly DateOnly TodayDate = new(2026, 11, 19);
    private static readonly Formatter F = new("da-DK", "Europe/Copenhagen");
    private static readonly IReadOnlyList<Stage> Stages = [new(1, "Qualification", 1, 10, "open"), new(2, "Needs analysis", 2, 25, "open"), new(3, "Proposal", 3, 50, "open"), new(4, "Negotiation", 4, 75, "open"), new(5, "Closed won", 5, 100, "won"), new(6, "Closed lost", 6, 0, "lost")];
    private static readonly Dictionary<string, decimal> ToDkk = new() { ["DKK"] = 1m, ["EUR"] = 7.46m, ["SEK"] = 0.685m, ["USD"] = 6.85m };
    private static readonly DemoDataset Data = DemoGenerator.Generate(TodayDate);
    private readonly CrmFactory _f = new();

    public void Dispose() => _f.Dispose();

    private static Row R(params (string, object?)[] kv) => new(kv.ToDictionary(x => x.Item1, x => x.Item2));

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Stamp(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss.000'Z'", CultureInfo.InvariantCulture);

    private static decimal Dkk(DemoOpportunity o) => o.Amount * ToDkk[o.Currency];

    private static string UserName(string key) => Data.Users.First(u => u.Key == key).Name;

    private static int AccountId(string key) => Data.Accounts.ToList().FindIndex(a => a.Key == key) + 1;

    private static string AccountName(string key) => Data.Accounts.First(a => a.Key == key).Name;

    private static string IndustryCode(string? label) => label switch { "Wind energy" => "wind", "Building materials" => "building", "Industry" => "industry", _ => "" };

    private static int OppId(DemoOpportunity o) => Data.Opportunities.ToList().IndexOf(o) + 1;

    private static Row OppRow(DemoOpportunity o)
    {
        var stage = Stages[Math.Min(o.StageSlot, 5)];
        return R(("Id", OppId(o)), ("Name", o.Name), ("AccountId", AccountId(o.AccountKey)), ("AccountName", AccountName(o.AccountKey)), ("StageId", stage.Id), ("StageName", stage.Name), ("StageType", stage.Type), ("Probability", stage.Probability),
            ("Amount", o.Amount), ("CurrencyCode", o.Currency), ("CloseDate", Iso(o.CloseDate)), ("OwnerName", UserName(o.OwnerKey)), ("CreatedAt", Stamp(o.CreatedAt)));
    }

    private static Row ActivityRow(DemoActivity a, int id, int? oppId = null) => R(("Id", id), ("ActivityType", a.Type), ("Subject", a.Subject), ("Description", a.Description), ("DueDate", a.DueDate is { } d ? Iso(d) : null), ("DueTime", a.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture)),
        ("IsDone", a.IsDone), ("OwnerName", UserName(a.OwnerKey)), ("OpportunityId", oppId ?? (a.OpportunityKey is null ? 0 : Data.Opportunities.ToList().FindIndex(o => o.Key == a.OpportunityKey) + 1)),
        ("OpportunityName", a.OpportunityKey is null ? "" : Data.Opportunities.First(o => o.Key == a.OpportunityKey).Name), ("AccountId", AccountId(a.AccountKey)), ("AccountName", AccountName(a.AccountKey)), ("CreatedAt", Stamp(a.CreatedAt)));

    private static string Spark(params double[] values)
    {
        var max = values.Max();
        return string.Join(' ', values.Select((v, i) => $"{(2 + 116.0 / (values.Length - 1) * i).ToString("0.##", CultureInfo.InvariantCulture)},{(30 - v / max * 26).ToString("0.##", CultureInfo.InvariantCulture)}"));
    }

    private static SparkResult SparkOf(string text, params double[] values)
    {
        var pts = Spark(values);
        var last = pts.Split(' ')[^1].Split(',');
        return new SparkResult(pts, last[0], last[1], text);
    }

    private static string Short(decimal v) => NumberFormats.CompactMoney(v, "DKK", "da-DK");

    private static string Full(decimal v) => F.Money(v, "DKK");

    [Fact]
    public async Task Dashboard_with_six_kpi_tiles_my_day_funnel_and_open_opportunities()
    {
        var open = Data.Opportunities.Where(o => o.IsOpen).ToList();
        var pipeline = open.Sum(Dkk);
        var won = Data.Opportunities.Where(o => o.StageSlot == DemoOpportunity.SlotWon && o.ClosedAt?.Month == 11 && o.ClosedAt?.Year == 2026).Sum(Dkk);
        var wonCount = Data.Opportunities.Count(o => o.StageSlot == DemoOpportunity.SlotWon && o.ClosedAt?.Month == 11 && o.ClosedAt?.Year == 2026);
        var avg = open.Sum(Dkk) / open.Count;
        var spark = SparkOf("Last 12 months: highest in Jun 2026, 3,1 mio. kr.", 1.2, 1.5, 1.1, 1.9, 2.2, 1.8, 2.6, 3.1, 2.4, 2.9, 2.7, 3.0);
        var perStage = Enumerable.Range(0, 4).Select(st => (Stage: Stages[st], Total: open.Where(o => o.StageSlot == st).Sum(Dkk))).ToList();
        var stack = perStage.Select((x, n) => new TileSeg(x.Stage.Name, n + 1, x.Total / pipeline * 100)).ToList();
        var weighted = perStage.Sum(x => x.Total * x.Stage.Probability / 100m);
        var qWon = Data.Opportunities.Where(o => o.StageSlot == DemoOpportunity.SlotWon && o.ClosedAt >= new DateTime(2026, 10, 1)).Sum(Dkk);
        const string Range = "01.11.2026–19.11.2026 compared with 01.10.2026–19.10.2026";
        var tiles = new List<TileVm>
        {
            new("Open pipeline", "/opportunities?status=open", Short(pipeline), Full(pipeline), $"{open.Count} opps", [], false, "", false, false, Stack: stack),
            new("Weighted pipeline", "/opportunities?status=open", Short(weighted), Full(weighted), "amount × stage probability", [], false, "", false, false, Meter: weighted / pipeline * 100, MeterText: $"{Math.Round(weighted / pipeline * 100)} % of the open pipeline"),
            new("Won this month", "/opportunities?status=won", Short(won), Full(won), $"{wonCount} deals", [], false, "", false, false, new TrendResult("up", "Up 12% vs same days last month", Range, "12%"), spark),
            new("Won this quarter", "/opportunities?status=won", Short(qWon), Full(qWon), "11 deals", [], false, "", false, false, new TrendResult("down", "Down 4% vs same days last quarter", Range, "4%"), SparkOf("Last 12 months: highest in Jun 2026, 3,1 mio. kr.", 1.4, 1.2, 1.9, 2.1, 1.7, 2.5, 3.0, 2.2, 2.8, 2.4, 2.9, 2.6)),
            new("Win rate (last 90 d)", "/analytics", "38 %", "", "19 of 50 closed deals won", [], false, "", false, false, new TrendResult("up", "Up 3 pts vs previous 90 days", Range, "3 pts"), Meter: 38),
            new("My activities due", "/activities?status=open", "3 overdue", "", "6 due today", [], false, "", true, true,
                Chips: [new("Overdue", 3, "/activities?due=overdue", "error"), new("Today", 6, "/activities?due=today", "clock"), new("Next 7 days", 11, "/activities?due=week", "calendar")]),
        };
        var overdue = new[] { 0, 1, 2 }.Select(i => (Data.Activities.Where(a => !a.IsDone && a.DueDate < TodayDate && a.Type != "note").ElementAt(i * 5), i)).ToList();
        var todays = Data.Activities.Where(a => !a.IsDone && a.DueDate >= TodayDate && a.Type != "note").Take(3).ToList();
        var overdueItems = overdue.Select(x => TimelineBuilder.ActivityItem(ActivityRow(x.Item1, 100 + x.i), F, Today, "/")).ToList();
        var todayItems = todays.Select((a, i) => TimelineBuilder.ActivityItem(ActivityRow(a with { DueDate = TodayDate }, 200 + i), F, Today, "/")).ToList();
        var weekItems = Data.Activities.Where(a => !a.IsDone && a.Type != "note").Skip(40).Take(3).Select((a, i) => TimelineBuilder.ActivityItem(ActivityRow(a with { DueDate = TodayDate.AddDays(2 + i * 2) }, 400 + i), F, Today, "/")).ToList();
        var funnel = Enumerable.Range(0, 4).Select(s => (Stage: Stages[s], Opps: open.Where(o => o.StageSlot == s).ToList())).ToList();
        var maxAmount = funnel.Max(f => f.Opps.Sum(Dkk));
        var funnelRows = funnel.Select(f => new FunnelRow(f.Stage.Name, $"/opportunities?status=open&stage={f.Stage.Id}", f.Opps.Count, Short(f.Opps.Sum(Dkk)), Full(f.Opps.Sum(Dkk)), Math.Round(f.Opps.Sum(Dkk) / maxAmount * 100m))).ToList();
        var mine = open.Where(o => o.OwnerKey == "lars").Concat(open).Distinct().Take(8).OrderBy(o => o.CloseDate).ToList();
        var oppCells = mine.Select(o => (IReadOnlyList<Cell>)EntityViews.CellsFor("opportunities", OppRow(o), F, Today, "/", Stages).Where((_, i) => i != 5).ToList()).ToList();
        var model = PageRenderHarness.Model<Crm.Web.Pages.Dashboard.IndexModel>(("Greeting", "Good morning, Lars"), ("AsOf", "19.11.2026 09:14"), ("LongDate", "Thursday 19 November"), ("FirstName", "Lars"), ("Tiles", tiles), ("UserId", 7), ("BaseCurrency", "DKK"),
            ("OppColumns", new[] { "Name", "Account", "Stage", "Amount", "Close date" }), ("Opps", oppCells), ("OppTotal", 21),
            ("Upcoming", new List<UpcomingGroup> { new("Overdue", 3, overdueItems, "/activities?due=overdue", "View all overdue activities"), new("Today", 3, todayItems, "/activities?due=today", "View all activities due today"), new("Next 7 days", 11, weekItems, "/activities?due=week", "View all activities in the next 7 days") }),
            ("Funnel", funnelRows), ("FunnelNotes", Array.Empty<string>()), ("Summary", "You have 3 overdue and 6 due today"), ("ViewAllDueHref", "/activities?due=due"));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Dashboard/Index", model, PageRenderHarness.User(CrmRole.Manager, "Lars Eriksen"), new Dictionary<string, object?> { ["ActiveNav"] = "dashboard", ["Title"] = "Dashboard" }, dumpSuffix: "v2-dashboard");
        Assert.Equal(6, doc.QuerySelectorAll("li.tile").Length);
        Assert.Equal(2, doc.QuerySelectorAll("li.tile polygon").Length);
        Assert.Equal(4, doc.QuerySelectorAll("li.tile svg.stack rect").Length);
        Assert.Equal(3, doc.QuerySelectorAll("li.tile .count-chip").Length);
        Assert.Equal("You have 3 overdue and 6 due today", doc.QuerySelector(".summary-pill")!.TextContent);
        Assert.Equal(9, doc.QuerySelectorAll("form.tl-action .check-btn").Length);
        Assert.NotNull(doc.QuerySelector("button[role=switch][aria-checked=false]"));
    }

    [Fact]
    public async Task Pipeline_board_with_four_full_columns()
    {
        var options = Stages.Take(4).Select(s => new MoveOption(s.Id, s.Name, false)).ToList();
        var columns = new List<BoardColumn>();
        var maxTotal = Enumerable.Range(0, 4).Max(s => Data.Opportunities.Where(o => o.StageSlot == s).Sum(Dkk));
        for (var s = 0; s < 4; s++)
        {
            var opps = Data.Opportunities.Where(o => o.StageSlot == s).OrderBy(o => o.CloseDate).ToList();
            var cards = opps.Take(5).Select(o => new DealCard(OppId(o), o.Name, AccountName(o.AccountKey), AccountId(o.AccountKey), F.Money(o.Amount, o.Currency), F.Date(Iso(o.CloseDate)), o.CloseDate < TodayDate, UserName(o.OwnerKey), "AAAAAAAAB9E=", false,
                options.Select(x => x with { Current = x.StageId == Stages[s].Id }).ToList())).ToList();
            var total = opps.Sum(Dkk);
            columns.Add(new BoardColumn(Stages[s].Id, Stages[s].Name, Stages[s].Probability + " %", opps.Count, Short(total), Full(total), Short(total * Stages[s].Probability / 100m), Math.Round(total / maxTotal * 100m), [], cards, $"/opportunities?status=open&stage={Stages[s].Id}", opps.Count > 5));
        }
        var all = Data.Opportunities.Where(o => o.IsOpen).ToList();
        var model = PageRenderHarness.Model<Crm.Web.Pages.Pipeline.IndexModel>(("Columns", columns), ("Filters", new List<FilterField> { new("owner", "Owner", [new SelectOption("", "All I can see"), new SelectOption("7", "Lars Eriksen")], "") }),
            ("TotalShort", Short(all.Sum(Dkk))), ("TotalFull", Full(all.Sum(Dkk))), ("TotalCount", all.Count), ("Notes", Array.Empty<string>()));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Pipeline/Index", model, PageRenderHarness.User(CrmRole.Manager, "Lars Eriksen"), new Dictionary<string, object?> { ["ActiveNav"] = "pipeline", ["Title"] = "Pipeline" }, dumpSuffix: "v2-board");
        Assert.Equal(4, doc.QuerySelectorAll("section.board-col").Length);
        Assert.Equal(20, doc.QuerySelectorAll("li.deal-card").Length);
    }

    [Fact]
    public async Task Opportunity_record_with_an_eight_item_timeline()
    {
        var opp = Data.Opportunities.First(o => o.StageSlot == 2 && o.Amount > 200_000m);
        var id = OppId(opp);
        var acts = Data.Activities.ToList();
        var rows = new List<Row>
        {
            ActivityRow(acts.First(a => !a.IsDone && a.Type == "task") with { DueDate = TodayDate.AddDays(-4), Subject = "Send revised quote" }, 301, id),
            ActivityRow(acts.First(a => !a.IsDone && a.Type == "call") with { DueDate = TodayDate.AddDays(1), DueTime = new TimeOnly(10, 30), Subject = "Walk through the proposal" }, 302, id),
            ActivityRow(acts.First(a => !a.IsDone && a.Type == "meeting") with { DueDate = TodayDate.AddDays(6), DueTime = new TimeOnly(14, 0), Subject = "On-site visit" }, 303, id),
            ActivityRow(acts.First(a => a.IsDone && a.Type == "meeting") with { DueDate = TodayDate.AddDays(-5), Subject = "Technical review with engineering", Description = "Went through blade root tolerances and delivery windows. They want a second sample batch before signing." }, 304, id),
            ActivityRow(acts.First(a => a.IsDone && a.Type == "call") with { DueDate = TodayDate.AddDays(-9), Subject = "Pricing call" }, 305, id),
            ActivityRow(acts.First(a => a.Type == "note") with { DueDate = null, Subject = "Competitor is quoting 6% lower", Description = "Procurement mentioned a competing quote. Margin is still fine at our price." }, 306, id),
            ActivityRow(acts.First(a => a.IsDone && a.Type == "task") with { DueDate = TodayDate.AddDays(-14), Subject = "Prepare NDA" }, 307, id),
            ActivityRow(acts.Last(a => a.IsDone && a.Type == "call") with { DueDate = TodayDate.AddDays(-30), Subject = "Intro call" }, 308, id),
        };
        var history = new List<HistoryRow> { new("2026-11-12T09:12:00.000Z", UserName(opp.OwnerKey), "U", "StageId", "Needs analysis", "Proposal"), new("2026-10-02T08:00:00.000Z", UserName(opp.OwnerKey), "C", null, null, null) };
        var groups = TimelineBuilder.Build(rows, history, F, Today, $"/opportunities/{id}", "opportunities", id);
        var row = OppRow(opp);
        row.Set("Probability", 50); row.Set("OwnerId", 7); row.Set("TeamId", 3); row.Set("PrimaryContactId", 5); row.Set("PrimaryContactName", "Katrine Bruun"); row.Set("LeadSourceLabel", "Trade show");
        var def = EntityDefinitions.All["opportunities"];
        var model = PageRenderHarness.Model<DetailModel>(("Def", def), ("Row", row), ("RecordName", opp.Name), ("Details", EntityViews.DetailRows("opportunities", row, F)), ("CanDelete", true), ("CreatedAt", "02.10.2026 10:00"), ("CreatedBy", UserName(opp.OwnerKey)), ("Edited", true),
            ("ModifiedAt", "18.11.2026 14:02"), ("ModifiedBy", "Lars Eriksen"), ("LogHref", $"/activities/new?opportunityId={id}"), ("Timeline", groups), ("ActivityCount", rows.Count), ("ShowActivityTab", true),
            ("Tabs", new List<TabInfo> { new("activity", "Activity", rows.Count, $"/opportunities/{id}?tab=activity", true) }),
            ("Facts", new List<Fact> { new("Amount", F.Money(opp.Amount, opp.Currency), Full(Dkk(opp))), new("Close date", F.Date(Iso(opp.CloseDate)), "in 26 days"), new("Probability", "50 %", "Weighted " + Short(Dkk(opp) / 2)), new("Lead source", "Trade show") }),
            ("Track", new StageTrack([new("Qualification", "passed"), new("Needs analysis", "passed"), new("Proposal", "current"), new("Negotiation", "later")], "open", "Proposal", "", 3, 4)),
            ("StageActions", new StageActions("AAAAAAAAB9E=", [new PicklistValue(1, null, "Price", true), new PicklistValue(2, null, "Timing", true)], "")));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Entity/Detail", model, PageRenderHarness.User(CrmRole.Manager, "Lars Eriksen"), new Dictionary<string, object?> { ["ActiveNav"] = "opportunities", ["Title"] = opp.Name }, dumpSuffix: "v2-opportunity");
        Assert.True(doc.QuerySelectorAll(".tl-item").Length >= 8);
    }

    [Fact]
    public async Task Accounts_list_with_a_full_page_of_rows()
    {
        var accounts = Data.Accounts.Take(14).Select((a, i) => R(("Id", i + 1), ("Name", a.Name), ("IndustryLabel", a.Industry ?? ""), ("IndustryCode", IndustryCode(a.Industry)), ("City", a.City), ("OwnerName", UserName(a.OwnerKey)),
            ("OpenPipeline", Full(Data.Opportunities.Where(o => o.IsOpen && o.AccountKey == a.Key).Sum(Dkk)) is var p && p != Full(0) ? p : ""))).ToList();
        var def = EntityDefinitions.All["accounts"];
        var q = new ListQuery("", 1, 25, def.DefaultSort, def.DefaultDir, new Dictionary<string, string>(), Today);
        var columns = EntityViews.ColumnsFor("accounts");
        var model = PageRenderHarness.Model<Crm.Web.Pages.Entity.IndexModel>(("Def", def), ("Total", Data.Accounts.Count), ("Q", q), ("IsFiltered", false), ("Columns", columns),
            ("Heads", columns.Select(c => new Head(c.Label, c.Sort is null ? null : "/accounts?sort=" + c.Sort, c.Sort == def.DefaultSort ? "ascending" : "none", c.Label == "Open pipeline")).ToList()),
            ("RowActions", accounts.Select(r => new RowAction(r.S("Name"), $"/accounts/{r.Int("Id")}/edit")).ToList()),
            ("Rows", accounts.Select(r => EntityViews.CellsFor("accounts", r, F, Today, "/accounts", Stages)).ToList()),
            ("Filters", new List<FilterField> { new("owner", "Owner", [new SelectOption("", "All"), new SelectOption("7", "Lars Eriksen")], ""), new("industry", "Industry", [new SelectOption("", "All")], "") }),
            ("ActiveFilters", new List<(string, string)>()), ("PageNo", 1), ("PageCount", 4), ("NextHref", "/accounts?page=2"), ("From", 1), ("To", accounts.Count));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Entity/Index", model, PageRenderHarness.User(CrmRole.Manager, "Lars Eriksen"), new Dictionary<string, object?> { ["ActiveNav"] = "accounts", ["Title"] = "Accounts" }, dumpSuffix: "v2-accounts");
        Assert.Equal(14, doc.QuerySelectorAll("table.data tbody tr").Length);
        Assert.NotNull(doc.QuerySelector(".chip.segment"));
    }

    [Fact]
    public async Task Sign_in_page()
    {
        var doc = await PageRenderHarness.RenderAsync(_f, "/Signin", PageRenderHarness.Model<SigninModel>(("ReturnTo", "/")), dumpSuffix: "v2-signin");
        Assert.NotNull(doc.QuerySelector(".signin-brand img.logo-negative"));
    }
}
