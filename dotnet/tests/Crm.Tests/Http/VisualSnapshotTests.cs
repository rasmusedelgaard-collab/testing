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
        var monthly = Enumerable.Range(0, 12).Select(i => TodayDate.AddMonths(i - 11)).Select(m => (double)Data.Opportunities.Where(o => o.StageSlot == DemoOpportunity.SlotWon && o.ClosedAt is { } c && c.Year == m.Year && c.Month == m.Month).Sum(Dkk)).ToArray();
        var spark = SparkOf("Last 12 months: highest in " + TodayDate.AddMonths(Array.IndexOf(monthly, monthly.Max()) - 11).ToString("MMM yyyy", CultureInfo.InvariantCulture), monthly);
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
            new("Won this quarter", "/opportunities?status=won", Short(qWon), Full(qWon), "11 deals", [], false, "", false, false, new TrendResult("down", "Down 4% vs same days last quarter", Range, "4%"), spark),
            new("Win rate (last 90 d)", "/analytics", "38 %", "", "19 of 50 closed deals won", [], false, "", false, false, new TrendResult("up", "Up 3 pts vs previous 90 days", Range, "3 pts"), Meter: 38),
            new("My activities due", "/activities?status=open", "3 overdue", "", "Next: Site visit at Nordvind Rotor ApS 10:00", [], false, "", true, true,
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
        var mine = open.Where(o => o.OwnerKey == "lars").Concat(open).Distinct().Take(7).OrderBy(o => o.CloseDate).ToList();
        var oppList = mine.Select(o => { var r = OppRow(o); var (idx, cnt) = (o.StageSlot + 1, 4); return new OppRowVm(r.Int("Id"), o.Name, r.Int("AccountId"), r.S("AccountName"), r.S("StageName"), r.S("StageType"), idx, cnt, NumberFormats.CompactMoney(o.Amount, o.Currency, "da-DK"), F.Money(o.Amount, o.Currency), F.Date(Iso(o.CloseDate)), o.CloseDate < TodayDate); }).ToList();
        var model = PageRenderHarness.Model<Crm.Web.Pages.Dashboard.IndexModel>(("Greeting", "Good morning, Lars"), ("AsOf", "19.11.2026 09:14"), ("LongDate", "Thursday 19 November"), ("FirstName", "Lars"), ("Tiles", tiles), ("UserId", 7), ("BaseCurrency", "DKK"),
            ("OppList", oppList), ("OppTotal", 21),
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
            var cards = opps.Take(5).Select(o => new DealCard(OppId(o), o.Name, AccountName(o.AccountKey), AccountId(o.AccountKey), F.Money(o.Amount, o.Currency), NumberFormats.CompactMoney(o.Amount, o.Currency, "da-DK"), F.Date(Iso(o.CloseDate)), o.CloseDate < TodayDate, UserName(o.OwnerKey), "AAAAAAAAB9E=", false,
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
        Assert.NotNull(doc.QuerySelector(".signin-card img.logo-light"));
    }

    private static Crm.Web.Pages.Entity.IndexModel ListOf(string entity, IReadOnlyList<Row> rows, int total, string sortCol, string searchHint, List<FilterField> filters, string stageFilterLabel = "")
    {
        var def = EntityDefinitions.All[entity];
        var q = new ListQuery("", 1, 25, def.DefaultSort, def.DefaultDir, new Dictionary<string, string>(), Today);
        var columns = EntityViews.ColumnsFor(entity);
        return PageRenderHarness.Model<Crm.Web.Pages.Entity.IndexModel>(("Def", def), ("Total", total), ("Q", q), ("IsFiltered", false), ("Columns", columns),
            ("Heads", columns.Select(c => new Head(c.Label, c.Sort is null ? null : $"{def.Path}?sort=" + c.Sort, c.Sort == def.DefaultSort ? "ascending" : "none", c.Label is "Amount" or "Open pipeline")).ToList()),
            ("RowActions", rows.Select(r => new RowAction(EntityViews.RecordName(entity, r), $"{def.Path}/{r.Int("Id")}/edit")).ToList()),
            ("Rows", rows.Select(r => EntityViews.CellsFor(entity, r, F, Today, def.Path, Stages)).ToList()),
            ("Filters", filters), ("ActiveFilters", new List<(string, string)>()), ("PageNo", 1), ("PageCount", (total + 24) / 25), ("NextHref", def.Path + "?page=2"), ("From", 1), ("To", rows.Count));
    }

    private static List<FilterField> Filters(params (string Name, string Label, string[] Options)[] f) =>
        f.Select(x => new FilterField(x.Name, x.Label, x.Options.Select((o, i) => new SelectOption(i == 0 ? "" : o, o)).ToList(), "")).ToList();

    [Fact]
    public async Task Opportunities_list_with_a_full_page_of_rows()
    {
        var rows = Data.Opportunities.Where(o => o.IsOpen).OrderBy(o => o.CloseDate).Take(14).Select(OppRow).ToList();
        var model = ListOf("opportunities", rows, 66, "close", "Search name, account...", Filters(("status", "Status", ["Open", "Won", "Lost"]), ("stage", "Stage", ["All", "Qualification", "Needs analysis"]), ("owner", "Owner", ["All", "Lars Eriksen"])));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Entity/Index", model, PageRenderHarness.User(CrmRole.Manager, "Lars Eriksen"), new Dictionary<string, object?> { ["ActiveNav"] = "opportunities", ["Title"] = "Opportunities" }, dumpSuffix: "v2-opps");
        Assert.Equal(14, doc.QuerySelectorAll("table.data tbody tr").Length);
        Assert.NotNull(doc.QuerySelector(".filters-form .search-field svg.icon"));
        Assert.Equal(4, doc.QuerySelectorAll(".filters .field:not(.search-field)").Length); // three filters and the page size
        Assert.NotNull(doc.QuerySelector("nav.pagination .pager"));
    }

    [Fact]
    public async Task Activities_list_with_a_full_page_of_rows()
    {
        var rows = Data.Activities.Where(a => !a.IsDone && a.Type != "note").Take(14).Select((a, i) => ActivityRow(a with { DueDate = TodayDate.AddDays(i - 3) }, 500 + i)).ToList();
        var model = ListOf("activities", rows, 212, "due", "Search subject...", Filters(("status", "Status", ["All", "Open", "Done"]), ("type", "Type", ["All", "Call", "Meeting", "Task", "Note"]), ("owner", "Owner", ["All", "Lars Eriksen"])));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Entity/Index", model, PageRenderHarness.User(CrmRole.Manager, "Lars Eriksen"), new Dictionary<string, object?> { ["ActiveNav"] = "activities", ["Title"] = "Activities" }, dumpSuffix: "v2-acts");
        Assert.Equal(14, doc.QuerySelectorAll("table.data tbody tr").Length);
    }

    [Fact]
    public async Task Account_record_with_tabs_facts_and_a_related_table()
    {
        var acc = Data.Accounts.First(a => Data.Opportunities.Count(o => o.AccountKey == a.Key && o.IsOpen) >= 3);
        var id = AccountId(acc.Key);
        var opps = Data.Opportunities.Where(o => o.AccountKey == acc.Key).OrderBy(o => !o.IsOpen).Take(5).ToList();
        var row = R(("Id", id), ("Name", acc.Name), ("IndustryLabel", acc.Industry ?? ""), ("IndustryCode", IndustryCode(acc.Industry)), ("Website", "https://example.test"), ("City", acc.City), ("CountryCode", "DK"), ("Street", "Industrivej 12"), ("PostalCode", "6700"), ("OwnerId", 7), ("OwnerName", UserName(acc.OwnerKey)), ("TeamId", 3));
        var tab = new TabTable("Opportunities", ["Name", "Stage", "Amount", "Close date"],
            opps.Select(o => (IReadOnlyList<Cell>)[new Cell(o.Name, $"/opportunities/{OppId(o)}", Role: "name"), new Cell(Stages[Math.Min(o.StageSlot, 5)].Name, Kind: "stage", Aux: Stages[Math.Min(o.StageSlot, 5)].Type, N: o.StageSlot + 1, Of: 4, Role: "k"), new Cell(F.Money(o.Amount, o.Currency), Right: true, Role: "k"), new Cell(F.Date(Iso(o.CloseDate)), Role: "k")]).ToList(), $"/opportunities/new?accountId={id}", "New opportunity");
        var def = EntityDefinitions.All["accounts"];
        var model = PageRenderHarness.Model<DetailModel>(("Def", def), ("Row", row), ("RecordName", acc.Name), ("Details", EntityViews.DetailRows("accounts", row, F)), ("CanDelete", true), ("CreatedAt", "02.10.2026 10:00"), ("CreatedBy", UserName(acc.OwnerKey)), ("Edited", false),
            ("LogHref", $"/activities/new?accountId={id}"), ("SegmentCode", IndustryCode(acc.Industry)), ("Timeline", new List<TimelineGroup>()),
            ("Tabs", new List<TabInfo> { new("activity", "Activity", 12, $"/accounts/{id}?tab=activity", false), new("contacts", "Contacts", 4, $"/accounts/{id}?tab=contacts", false), new("opportunities", "Opportunities", opps.Count, $"/accounts/{id}?tab=opportunities", true) }), ("TabTable", tab),
            ("Facts", new List<Fact> { new("Open opportunities", opps.Count(o => o.IsOpen).ToString(CultureInfo.InvariantCulture)), new("Open pipeline", Short(opps.Where(o => o.IsOpen).Sum(Dkk)), Full(opps.Where(o => o.IsOpen).Sum(Dkk))), new("Contacts", "4"), new("Last activity", "5 days ago") }));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Entity/Detail", model, PageRenderHarness.User(CrmRole.Manager, "Lars Eriksen"), new Dictionary<string, object?> { ["ActiveNav"] = "accounts", ["Title"] = acc.Name }, dumpSuffix: "v2-account");
        Assert.NotNull(doc.QuerySelector(".tabs a[aria-current=page]"));
        Assert.NotNull(doc.QuerySelector("dl.details"));
    }

    [Fact]
    public async Task Contact_record()
    {
        var c = Data.Contacts.First();
        var row = R(("Id", 5), ("FirstName", c.FirstName ?? ""), ("LastName", c.LastName), ("AccountId", AccountId(c.AccountKey!)), ("AccountName", AccountName(c.AccountKey!)), ("JobTitle", c.JobTitle), ("Email", "contact@example.test"), ("Phone", "+45 12 34 56 78"), ("OwnerId", 7), ("OwnerName", UserName("lars")), ("TeamId", 3));
        var def = EntityDefinitions.All["contacts"];
        var model = PageRenderHarness.Model<DetailModel>(("Def", def), ("Row", row), ("RecordName", EntityViews.RecordName("contacts", row)), ("Details", EntityViews.DetailRows("contacts", row, F)), ("CanDelete", true), ("CreatedAt", "02.10.2026 10:00"), ("CreatedBy", "Lars Eriksen"), ("Edited", false),
            ("LogHref", "/activities/new?contactId=5"), ("Timeline", new List<TimelineGroup>()), ("Tabs", new List<TabInfo> { new("activity", "Activity", 0, "/contacts/5?tab=activity", true) }), ("ShowActivityTab", true), ("Facts", new List<Fact> { new("Open opportunities", "2"), new("Last activity", "5 days ago") }));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Entity/Detail", model, PageRenderHarness.User(CrmRole.Manager, "Lars Eriksen"), new Dictionary<string, object?> { ["ActiveNav"] = "contacts" }, dumpSuffix: "v2-contact");
        Assert.NotNull(doc.QuerySelector(".page-header .avatar"));
    }

    [Fact]
    public async Task Opportunity_edit_form_with_an_inline_error()
    {
        var opp = Data.Opportunities.First(o => o.StageSlot == 2 && o.Amount > 200_000m);
        var opt = (string v, string l, bool sel) => new SelectOption(v, l, sel);
        var fields = new List<FormField>
        {
            new("Name", "Name (required)", "text", opp.Name, Required: true),
            new("AccountId", "Account (required)", "select", "1", Required: true, Options: [opt("1", AccountName(opp.AccountKey), true), opt("2", "Another account A/S", false)]),
            new("PrimaryContactId", "Primary contact", "select", "", Options: [opt("", "— none —", true), opt("5", "Katrine Bruun", false)]),
            new("StageId", "Stage (required)", "select", "3", Required: true, Options: Stages.Take(4).Select(x => opt(x.Id.ToString(CultureInfo.InvariantCulture), x.Name, x.Id == 3)).ToList()),
            new("Amount", "Amount (required)", "text", "1498000", Error: "Enter an amount, for example 12000.", Required: true, InputMode: "decimal"),
            new("CurrencyCode", "Currency (required)", "select", "DKK", Required: true, Options: [opt("DKK", "DKK", true), opt("EUR", "EUR", false)]),
            new("CloseDate", "Close date (required)", "date", "2027-03-29", Required: true),
            new("LeadSourceValueId", "Lead source", "select", "1", Options: [opt("", "— none —", false), opt("1", "Trade show", true)]),
            new("OwnerId", "Owner (required)", "select", "7", Required: true, Options: [opt("7", "Lars Eriksen", true)]),
        };
        var page = new FormPage($"Edit {opp.Name}", $"/opportunities/{OppId(opp)}", $"/opportunities/{OppId(opp)}", fields, [("Amount", "Enter an amount, for example 12000.")], "AAAAAAAAB9E=", "opportunities");
        var model = PageRenderHarness.Model<DetailModel>(("Form", page));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Entity/Detail", model, PageRenderHarness.User(CrmRole.Manager, "Lars Eriksen"), new Dictionary<string, object?> { ["ActiveNav"] = "opportunities", ["Title"] = "Edit" }, dumpSuffix: "v2-form");
        Assert.Equal(3, doc.QuerySelectorAll(".form-group").Length);
        Assert.NotNull(doc.QuerySelector(".form-group .has-error .field-error"));
    }

    [Fact]
    public async Task Analytics_with_five_chart_cards()
    {
        string Json(string kind, string vk, string[] labels, double[] data) => System.Text.Json.JsonSerializer.Serialize(new { kind, labels, data, valueKind = vk, seriesLabel = "Series", locale = "da-DK", currency = "DKK" });
        ChartView Chart(string id, string title, string kind, string vk, string take, string[] labels, double[] data, string[] cols)
        {
            var rows = labels.Select((l, i) => new Crm.Web.Services.ChartTableRow([l, vk == "money" ? F.Money((decimal)data[i], "DKK") : vk == "percent" ? data[i] + " %" : data[i].ToString(CultureInfo.InvariantCulture)])).ToList();
            return new ChartView(new Crm.Web.Services.ChartVm(id, title, kind, vk, "Series", take, [], labels, data.Select(x => (decimal)x).ToList(), cols, rows, title + ". " + take, false), Json(kind, vk, labels, data));
        }
        var perStage = Enumerable.Range(0, 4).Select(st => (double)Data.Opportunities.Where(o => o.IsOpen && o.StageSlot == st).Sum(Dkk)).ToArray();
        var charts = new List<ChartView>
        {
            Chart("pipeline", "Open pipeline by stage", "bar", "money", "Qualification holds the most value.", ["Qualification", "Needs analysis", "Proposal", "Negotiation"], perStage, ["Stage", "Amount"]),
            Chart("won", "Won revenue by month", "line", "money", "Highest in September 2026.", ["Jul 2026", "Aug 2026", "Sep 2026", "Oct 2026", "Nov 2026"], [2100000, 3400000, 5200000, 3900000, 4600000], ["Month", "Amount"]),
            Chart("winrate", "Win rate by owner", "hbar", "percent", "Anna Kowalska wins 46 % of closed deals.", ["Anna Kowalska", "Lars Eriksen", "Jonas Weber", "Lise Bech"], [46, 38, 31, 27], ["Owner", "Win rate"]),
            Chart("acts", "Activities by type", "bar", "count", "Calls are the most common activity.", ["Call", "Meeting", "Task", "Note"], [64, 31, 48, 22], ["Type", "Activities"]),
            Chart("lead", "Opportunities by lead source", "hbar", "count", "Trade shows bring the most opportunities.", ["Trade show", "Referral", "Website", "Outbound"], [28, 19, 14, 9], ["Source", "Opportunities"]),
        };
        var model = PageRenderHarness.Model<Crm.Web.Pages.Dashboard.AnalyticsModel>(("Period", "this-quarter"), ("From", "2026-10-01"), ("To", "2026-12-31"), ("RangeText", "01.10.2026 – 31.12.2026"), ("OwnerValue", ""), ("TeamOptions", new List<(string, string)> { ("team:3", "Team: Wind") }),
            ("Owners", new List<(string, string)> { ("7", "Lars Eriksen") }), ("Charts", charts), ("BaseCurrency", "DKK"), ("Error", ""));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Dashboard/Analytics", model, PageRenderHarness.User(CrmRole.Manager, "Lars Eriksen"), new Dictionary<string, object?> { ["ActiveNav"] = "analytics", ["Title"] = "Analytics", ["Scripts"] = new[] { "/vendor/chart.umd.js", "/js/charts.js" } }, dumpSuffix: "v2-analytics");
        Assert.Equal(7, doc.QuerySelectorAll("fieldset.seg-radio input[type=radio]").Length);
        Assert.Equal("this-quarter", doc.QuerySelector("fieldset.seg-radio input:checked")!.GetAttribute("value"));
        Assert.Equal("f-period-custom", doc.QuerySelector("#f-from")!.GetAttribute("data-custom-for"));
        Assert.Equal(5, doc.QuerySelectorAll("figure.chart").Length);
    }

    [Fact]
    public async Task Settings_general_and_fx_pages()
    {
        var general = PageRenderHarness.Model<Crm.Web.Pages.Settings.GeneralModel>(("AppName", "CRM"), ("AccentColor", "#1A66CC"), ("SettingsNav", "general"));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Settings/General", general, PageRenderHarness.User(CrmRole.Admin, "Mette Holm"), new Dictionary<string, object?> { ["SettingsNav"] = "general", ["ActiveNav"] = "settings", ["Title"] = "General" }, dumpSuffix: "v2-general");
        Assert.NotNull(doc.QuerySelector(".settings-content form.panel"));
        var rates = new List<Crm.Web.Pages.Settings.FxRowVm>();
        foreach (var (cur, rate) in new[] { ("EUR", 7.46m), ("SEK", 0.685m), ("USD", 6.85m) })
        {
            for (var m = 0; m < 3; m++) { rates.Add(new(new Crm.Web.Data.FxRateRec(rates.Count + 1, cur, $"2026-{11 - m:00}", rate - m * 0.01m, "Mette Holm", "2026-11-01T08:00:00.000Z"), $"{(m == 0 ? "Nov" : m == 1 ? "Oct" : "Sep")} 2026", (rate - m * 0.01m).ToString("0.0000", CultureInfo.GetCultureInfo("da-DK")), $"0{m + 1}.11.2026 08:00")); }
        }
        var fx = PageRenderHarness.Model<Crm.Web.Pages.Settings.FxModel>(("Base", "DKK"), ("Currencies", new List<string> { "EUR", "SEK", "USD" }), ("Years", new List<string> { "2026" }), ("CurMonth", "2026-11"), ("CurLabel", "Nov 2026"), ("Rows", rates), ("Missing", new List<string> { "PLN" }), ("Errors", new Dictionary<string, string>()), ("MonthValue", "2026-11"));
        var fxDoc = await PageRenderHarness.RenderAsync(_f, "/Settings/Fx", fx, PageRenderHarness.User(CrmRole.Admin, "Mette Holm"), new Dictionary<string, object?> { ["SettingsNav"] = "fx", ["ActiveNav"] = "settings", ["Title"] = "FX rates" }, dumpSuffix: "v2-fx");
        Assert.NotNull(fxDoc.QuerySelector("table.data"));
    }

    [Fact]
    public async Task Test_user_picker_and_not_found_page()
    {
        var users = Data.Users.Take(5).Select((u, i) => new Crm.Web.Data.TestUserRow(i + 1, "00000000-0000-4000-8000-00000000a00" + i, u.Name, null, i == 0 ? CrmRole.Admin : i < 3 ? CrmRole.Manager : CrmRole.User, 3, i % 2 == 0 ? "Wind" : "Building materials")).ToList();
        var picker = PageRenderHarness.Model<Crm.Web.Pages.Auth.TestLoginModel>(("ShowPicker", true), ("Users", users), ("ReturnTo", "/"));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Auth/TestLogin", picker, dumpSuffix: "v2-picker");
        Assert.Equal(5, doc.QuerySelectorAll(".signin-card .user-cards .user-card").Length);
        Assert.Equal(5, doc.QuerySelectorAll(".user-card .avatar").Length);
        Assert.NotNull(doc.QuerySelector(".user-card input[type=radio][checked]"));
        var notFound = PageRenderHarness.Model<ErrorModel>(("Heading", "We can't find that page or record."), ("Message", "It may have been deleted, or you may not have access."), ("Reference", "7K3F9Q"), ("Status", 404), ("PageTitle", "Not found"));
        var nf = await PageRenderHarness.RenderAsync(_f, "/Errors/Error", notFound, PageRenderHarness.User(CrmRole.Manager, "Lars Eriksen"), dumpSuffix: "v2-404");
        Assert.NotNull(nf.QuerySelector(".system-block h1"));
    }

    [Fact]
    public async Task Search_results_page()
    {
        var accounts = Data.Accounts.Where(a => a.Name.Contains("Nord", StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
        var opps = Data.Opportunities.Where(o => o.IsOpen).Take(4).ToList();
        var groups = new List<SearchGroup>
        {
            new("accounts", "Accounts", "account", accounts.Count, accounts.Select(a => new SearchHit(a.Name, $"/accounts/{AccountId(a.Key)}", a.City + ", " + (a.Industry ?? ""), UserName(a.OwnerKey), Icon: "accounts")).ToList(), "/accounts?q=nord"),
            new("opportunities", "Opportunities", "opportunity", 9, opps.Select(o => new SearchHit(o.Name, $"/opportunities/{OppId(o)}", F.Money(o.Amount, o.Currency) + " · " + AccountName(o.AccountKey), UserName(o.OwnerKey), Stages[Math.Min(o.StageSlot, 5)].Name, "open", o.StageSlot + 1, 4, "opportunities")).ToList(), "/opportunities?q=nord"),
        };
        var doc = await PageRenderHarness.RenderAsync(_f, "/Search", PageRenderHarness.Model<SearchModel>(("Text", "nord"), ("Groups", groups), ("TotalResults", 12)), PageRenderHarness.User(CrmRole.Manager, "Lars Eriksen"), new Dictionary<string, object?> { ["ActiveNav"] = "" }, dumpSuffix: "v2-search");
        Assert.Equal(2, doc.QuerySelectorAll(".result-group.panel").Length);
        Assert.NotNull(doc.QuerySelector(".search-page .search-field svg.icon"));
    }
}
