using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Pages;
using Crm.Web.Pages.Dashboard;
using Crm.Web.Pages.Entity;
using Crm.Web.Pages.Pipeline;
using Crm.Web.Services;

namespace Crm.Tests.Http;

/// <summary>
/// Renders the full pages (layouts, partials, every branch of the markup) from in-memory models in every CI run, without a database.
/// The SQL-backed tests then only have to prove the data, not the Razor logic.
/// </summary>
public sealed class PageRenderSmokeTests : IDisposable
{
    private readonly CrmFactory _f = new();
    private static readonly Formatter F = new("da-DK", "Europe/Copenhagen");
    private static readonly IReadOnlyList<Stage> Stages = [new(1, "Qualification", 1, 10, "open"), new(2, "Needs analysis", 2, 25, "open"), new(3, "Proposal", 3, 50, "open"), new(4, "Negotiation", 4, 75, "open"), new(5, "Closed won", 5, 100, "won"), new(6, "Closed lost", 6, 0, "lost")];

    public void Dispose() => _f.Dispose();

    private static Row R(params (string, object?)[] kv) => new(kv.ToDictionary(x => x.Item1, x => x.Item2));

    private static Row Opp(int id = 7, int stage = 3, string type = "open", string stageName = "Proposal", string close = "2026-12-15") => R(("Id", id), ("Name", "Blade root seals 2027"), ("AccountId", 3), ("AccountName", "Nordvind Rotor A/S"), ("PrimaryContactId", 5), ("PrimaryContactName", "Lars Berg"),
        ("StageId", stage), ("StageName", stageName), ("StageType", type), ("Probability", 50), ("Amount", 240000m), ("CurrencyCode", "EUR"), ("CloseDate", close), ("LeadSourceLabel", "Event"), ("LostReasonLabel", "Price"),
        ("OwnerId", 7), ("OwnerName", "Anna Holm"), ("TeamId", 3), ("CreatedBy", 7), ("CreatedByName", "Anna Holm"), ("CreatedAt", "2026-10-01T08:00:00.000Z"), ("ModifiedBy", 7), ("ModifiedByName", "Mette Holm"), ("ModifiedAt", "2026-11-18T13:02:00.000Z"), ("RowVer", "AAAAAAAAB9E="));

    [Fact]
    public async Task Dashboard_renders_tiles_trends_sparkline_my_day_funnel_and_my_opportunities()
    {
        var spark = new SparkResult("2,28 12,26 22,20", "22", "20", "Last 12 months: highest in Jun 2026, 3,1 mio. kr.");
        var tiles = new List<TileVm>
        {
            new("Open pipeline", "/opportunities?status=open", "24,1 mio. kr.", "DKK 24.100.000,00", "66 opps", ["1 opportunity in SEK not included: no FX rate"], true, "SEK", false, false),
            new("Won this month", "/opportunities?status=won", "2,4 mio. kr.", "DKK 2.400.000,00", "5 deals", [], false, "", false, false, new TrendResult("up", "Up 12% vs same days last month", "01.11.2026–19.11.2026 compared with 01.10.2026–19.10.2026"), spark),
            new("Activities due", "/activities?status=open", "2 overdue", "", "3 due today", [], false, "", true, true),
        };
        var item = TimelineBuilder.ActivityItem(R(("Id", 4), ("ActivityType", "call"), ("Subject", "Follow up"), ("DueDate", "2026-11-17"), ("IsDone", false), ("OwnerName", "Anna Holm"), ("OpportunityId", 7), ("OpportunityName", "Blade root seals 2027"), ("CreatedAt", "2026-11-01T08:00:00.000Z")), F, "2026-11-19", "/");
        var oppCells = EntityViews.CellsFor("opportunities", Opp(), F, "2026-11-19", "/", Stages).Where((_, i) => i != 5).ToList();
        var model = PageRenderHarness.Model<Crm.Web.Pages.Dashboard.IndexModel>(("Greeting", "Good morning, Mette"), ("AsOf", "19.11.2026 09:14"), ("LongDate", "Thursday 19 November"), ("FirstName", "Mette"), ("Tiles", tiles), ("UserId", 7), ("BaseCurrency", "DKK"),
            ("OppColumns", new[] { "Name", "Account", "Stage", "Amount", "Close date" }), ("Opps", new List<IReadOnlyList<Cell>> { oppCells }), ("OppTotal", 12),
            ("Upcoming", new List<UpcomingGroup> { new("Overdue", 2, [item], "/activities?due=overdue", "View all overdue activities"), new("Today", 0, [], "/x", "View all activities due today") }),
            ("Funnel", new List<FunnelRow> { new("Qualification", "/opportunities?status=open&stage=1", 25, "6,0 mio. kr.", "DKK 6.000.000,00", 83), new("Needs analysis", "/opportunities?status=open&stage=2", 18, "7,2 mio. kr.", "DKK 7.200.000,00", 100) }),
            ("FunnelNotes", new[] { "Uses latest rate for 1 currency" }));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Dashboard/Index", model, PageRenderHarness.User(), new Dictionary<string, object?> { ["ActiveNav"] = "dashboard" }, dumpSuffix: "full");
        Assert.Equal("Good morning, Mette", doc.QuerySelector("h1")!.TextContent);
        Assert.Equal(3, doc.QuerySelectorAll("li.tile").Length);
        Assert.Contains("(DKK 24.100.000,00)", doc.QuerySelector("li.tile .tile-value")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Ask an administrator.", doc.QuerySelector(".tile-note")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Up 12% vs same days last month", doc.QuerySelector(".tile-trend")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("highest in Jun 2026", doc.QuerySelector("svg.spark + .visually-hidden")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("overdue", doc.QuerySelector(".tile-value.danger-text .tile-unit")!.TextContent);
        Assert.Equal("My day", doc.QuerySelector("#upcoming-h")!.TextContent);
        Assert.Equal("Overdue (2)", doc.QuerySelector("section[aria-labelledby=upcoming-h] h3")!.TextContent);
        Assert.Contains("2 days overdue", doc.QuerySelector("section[aria-labelledby=upcoming-h] .chip.danger")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("View all my opportunities (12)", doc.QuerySelector("section[aria-labelledby=my-opps-h] p a")!.TextContent);
        Assert.Equal("Open board", doc.QuerySelector("section[aria-labelledby=funnel-h] .panel-head a")!.TextContent);
        Assert.Equal(2, doc.QuerySelectorAll("table.funnel tbody tr").Length);
        Assert.Equal("Amounts in DKK at this month's FX rates.", doc.QuerySelector(".funnel-foot")!.TextContent);
        Assert.NotNull(doc.QuerySelector(".chip.stage .pip.on"));
        Assert.NotNull(doc.QuerySelector("nav.sidebar a.nav-item[aria-current=page][href='/']"));
        Assert.Null(doc.QuerySelector("a.nav-item[href='/settings/general']")); // a Manager does not get Settings
        Assert.Equal("Role: Manager", doc.QuerySelector(".badge")!.TextContent);
        Assert.NotNull(doc.QuerySelector("form.theme-group button[aria-pressed=true]"));
        Assert.Equal("Skip to main content", doc.QuerySelector("a.skip-link")!.TextContent);
        Assert.StartsWith("©", doc.QuerySelector("footer.app-footer")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_empty_states_and_admin_settings_link()
    {
        var model = PageRenderHarness.Model<Crm.Web.Pages.Dashboard.IndexModel>(("Greeting", "Good evening, Test"), ("Tiles", new List<TileVm>()), ("Upcoming", new List<UpcomingGroup>()), ("Funnel", new List<FunnelRow>()), ("Opps", new List<IReadOnlyList<Cell>>()), ("OppColumns", Array.Empty<string>()));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Dashboard/Index", model, PageRenderHarness.User(CrmRole.Admin));
        Assert.Contains("Nothing due. You're all caught up.", doc.QuerySelector("section[aria-labelledby=upcoming-h]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("No open opportunities yet.", doc.QuerySelector("section[aria-labelledby=my-opps-h]")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(doc.QuerySelector("a.nav-item[href='/settings/general']"));
    }

    private static Crm.Web.Pages.Entity.IndexModel ListModel(string entity, IReadOnlyList<Row> rows, bool filtered = false)
    {
        var def = EntityDefinitions.All[entity];
        var q = new ListQuery("", 1, 25, def.DefaultSort, def.DefaultDir, new Dictionary<string, string> { ["status"] = "open" }, "2026-11-19");
        var columns = EntityViews.ColumnsFor(entity);
        return PageRenderHarness.Model<Crm.Web.Pages.Entity.IndexModel>(("Def", def), ("Total", 128), ("Q", q), ("IsFiltered", filtered), ("Columns", columns),
            ("Heads", columns.Select(c => new Head(c.Label, c.Sort is null ? null : "/x?sort=" + c.Sort, c.Sort == def.DefaultSort ? "ascending" : "none", c.Label == "Amount")).ToList()),
            ("RowActions", rows.Select(r => new RowAction(EntityViews.RecordName(entity, r), $"{def.Path}/{r.Int("Id")}/edit")).ToList()),
            ("Rows", rows.Select(r => EntityViews.CellsFor(entity, r, F, "2026-11-19", def.Path, Stages)).ToList()),
            ("Filters", new List<FilterField> { new("owner", "Owner", [new SelectOption("", "All"), new SelectOption("7", "Anna Holm")], "7") }),
            ("ActiveFilters", new List<(string, string)> { ("Owner: Anna Holm", "/x") }), ("PageNo", 1), ("PageCount", 6), ("NextHref", "/x?page=2"), ("From", 1), ("To", 25));
    }

    [Fact]
    public async Task Opportunity_list_renders_chips_filters_actions_and_pagination()
    {
        var doc = await PageRenderHarness.RenderAsync(_f, "/Entity/Index", ListModel("opportunities", [Opp(close: "2026-10-01"), Opp(8, 5, "won", "Closed won")]), PageRenderHarness.User(), new Dictionary<string, object?> { ["Title"] = "Opportunities", ["ActiveNav"] = "opportunities" }, dumpSuffix: "opps");
        Assert.Equal("Opportunities (128)", doc.QuerySelector("h1")!.TextContent);
        Assert.Equal(2, doc.QuerySelectorAll("table.data tbody tr").Length);
        Assert.Contains("Past close date", doc.QuerySelector("table.data")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(doc.QuerySelector(".chip.won"));
        Assert.Equal("Edit Blade root seals 2027", doc.QuerySelector("td.act a")!.GetAttribute("aria-label"));
        Assert.Equal("Remove filter Owner: Anna Holm", doc.QuerySelector(".filter-chips a")!.GetAttribute("aria-label"));
        Assert.Equal("Board", doc.QuerySelector(".view-switch a:nth-child(2)")!.TextContent);
        Assert.Equal("ascending", doc.QuerySelector("th[aria-sort=ascending]")!.GetAttribute("aria-sort"));
        Assert.Contains("Showing 1–25 of 128", doc.QuerySelector("nav.pagination")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("page", doc.QuerySelector("a.nav-item[href='/opportunities']")!.GetAttribute("aria-current"));
        Assert.Contains("128 opportunities found", doc.QuerySelector("p[role=status]")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Activity_account_and_contact_lists_render_their_special_cells_and_empty_states()
    {
        var act = R(("Id", 4), ("ActivityType", "call"), ("Subject", "Follow up"), ("DueDate", "2026-11-17"), ("DueTime", "14:00"), ("IsDone", false), ("OwnerName", "Anna Holm"), ("OpportunityId", 7), ("OpportunityName", "Blade"), ("CreatedAt", "2026-11-01T08:00:00.000Z"));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Entity/Index", ListModel("activities", [act]), PageRenderHarness.User(), dumpSuffix: "acts");
        Assert.Equal("Mark 'Follow up' as done", doc.QuerySelector("td button")!.GetAttribute("aria-label"));
        Assert.Contains("Overdue", doc.QuerySelector(".chip.danger")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(doc.QuerySelector("td .icon-ring"));
        var account = R(("Id", 3), ("Name", "Nordvind"), ("IndustryLabel", "Wind"), ("IndustryCode", "wind"), ("City", "Esbjerg"), ("OwnerName", "Anna Holm"), ("OpenPipeline", "DKK 1,00"));
        var accounts = await PageRenderHarness.RenderAsync(_f, "/Entity/Index", ListModel("accounts", [account]), PageRenderHarness.User());
        Assert.NotNull(accounts.QuerySelector(".chip.segment.wind"));
        var contact = R(("Id", 5), ("FirstName", "Lars"), ("LastName", "Berg"), ("AccountId", 3), ("AccountName", "Nordvind"), ("JobTitle", "CTO"), ("Email", "lars@example.test"), ("Phone", "1"), ("OwnerName", "Anna Holm"));
        var contacts = await PageRenderHarness.RenderAsync(_f, "/Entity/Index", ListModel("contacts", [contact]), PageRenderHarness.User());
        Assert.Equal("mailto:lars@example.test", contacts.QuerySelector("a[href^=mailto]")!.GetAttribute("href"));
        var empty = await PageRenderHarness.RenderAsync(_f, "/Entity/Index", ListModel("opportunities", [], filtered: false), PageRenderHarness.User());
        Assert.Contains("No opportunities yet.", empty.QuerySelector(".empty")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Opportunities track deals from first contact to close.", empty.QuerySelector(".empty")!.TextContent, StringComparison.Ordinal);
        var none = await PageRenderHarness.RenderAsync(_f, "/Entity/Index", ListModel("opportunities", [], filtered: true), PageRenderHarness.User());
        Assert.Contains("No opportunities match these filters.", none.QuerySelector(".empty")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(none.QuerySelector(".empty a.button.secondary"));
    }

    private static DetailModel DetailFor(string entity, Row row, params (string, object?)[] more)
    {
        var def = EntityDefinitions.All[entity];
        var f = F;
        var items = new List<(string, object?)>
        {
            ("Def", def), ("Row", row), ("RecordName", EntityViews.RecordName(entity, row)), ("Details", EntityViews.DetailRows(entity, row, f)), ("CanDelete", true),
            ("CreatedAt", "01.10.2026 10:00"), ("CreatedBy", "Anna Holm"), ("Edited", true), ("ModifiedAt", "18.11.2026 14:02"), ("ModifiedBy", "Mette Holm"),
            ("LogHref", "/activities/new?opportunityId=7"), ("Tabs", new List<TabInfo>()), ("Timeline", new List<TimelineGroup>()),
        };
        items.AddRange(more);
        return PageRenderHarness.Model<DetailModel>([.. items]);
    }

    [Fact]
    public async Task Opportunity_record_renders_track_facts_timeline_side_panels_and_more_menu()
    {
        var row = Opp();
        var groups = TimelineBuilder.Build([R(("Id", 4), ("ActivityType", "task"), ("Subject", "Send quote"), ("Description", new string('x', 250)), ("DueDate", "2026-11-10"), ("IsDone", false), ("OwnerName", "Anna Holm"), ("OpportunityId", 7), ("OpportunityName", "Blade"), ("ContactId", 5), ("ContactName", "Lars Berg"), ("CreatedAt", "2026-11-01T08:00:00.000Z"))],
            [new HistoryRow("2026-11-18T09:12:00.000Z", "Mette Holm", "U", "StageId", "Needs analysis", "Proposal"), new HistoryRow("2026-10-01T08:00:00.000Z", "Anna Holm", "C", null, null, null)], F, "2026-11-19", "/opportunities/7", "opportunities", 7);
        var model = DetailFor("opportunities", row, ("Timeline", groups), ("ActivityCount", 3), ("ShowActivityTab", true), ("Tabs", new List<TabInfo> { new("activity", "Activity", 3, "/opportunities/7?tab=activity", true) }),
            ("Facts", new List<Fact> { new("Amount", "EUR 240.000,00"), new("Close date", "15.12.2026", "in 26 days", "Past close date"), new("Probability", "50 %") }),
            ("Track", new StageTrack([new("Qualification", "passed"), new("Needs analysis", "passed"), new("Proposal", "current"), new("Negotiation", "later")], "open", "Proposal", "", 3, 4)),
            ("StageActions", new StageActions("AAAAAAAAB9E=", [new PicklistValue(1, null, "Price", true)], "")));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Entity/Detail", model, PageRenderHarness.User(), dumpSuffix: "opp");
        Assert.Equal("Blade root seals 2027", doc.QuerySelector("h1")!.TextContent);
        Assert.Equal("Log activity", doc.QuerySelector(".page-header a.button.primary")!.TextContent.Trim());
        Assert.Equal("Delete opportunity", doc.QuerySelector(".more a")!.TextContent);
        Assert.Equal("step", doc.QuerySelector("ol.stage-track li[aria-current]")!.GetAttribute("aria-current"));
        Assert.Contains("(current stage)", doc.QuerySelector("ol.stage-track li.current")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Stage 3 of 4: Proposal", doc.QuerySelector(".stage-line")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("EUR 240.000,00", doc.QuerySelector(".facts")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Past close date", doc.QuerySelector(".facts .chip.warn")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("Activity (3)", doc.QuerySelector("h2")!.TextContent);
        Assert.Equal(["Overdue", "November 2026", "October 2026"], doc.QuerySelectorAll("ol.timeline > li > h3").Select(h => h.TextContent));
        Assert.Contains("Show more", doc.QuerySelector(".tl-desc")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Primary contact", doc.QuerySelector("dl.details")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(doc.QuerySelector("#stage-h"));
        Assert.Equal("Mark as won", doc.QuerySelector("form[action$='/won'] button")!.TextContent);
        Assert.Contains("Created 01.10.2026 10:00 by Anna Holm · Last modified by Mette Holm, 18.11.2026 14:02", doc.QuerySelector("footer.record-footer")!.TextContent.Replace("\n", " ", StringComparison.Ordinal).Replace("  ", " ", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal("Opportunities", doc.QuerySelector(".breadcrumb a")!.TextContent);
    }

    [Fact]
    public async Task Lost_and_won_opportunities_show_the_reason_or_the_won_chip_instead_of_the_close_panel()
    {
        var lost = DetailFor("opportunities", Opp(8, 6, "lost", "Closed lost"), ("Track", new StageTrack([], "lost", "Closed lost", "Price", 0, 4)), ("ShowActivityTab", true));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Entity/Detail", lost, PageRenderHarness.User());
        Assert.Contains("Lost reason: Price", doc.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
        Assert.Null(doc.QuerySelector("#stage-h"));
        var won = DetailFor("opportunities", Opp(9, 5, "won", "Closed won"), ("Track", new StageTrack([new("Qualification", "passed")], "won", "Closed won", "", 0, 4)), ("ShowActivityTab", true));
        var wonDoc = await PageRenderHarness.RenderAsync(_f, "/Entity/Detail", won, PageRenderHarness.User());
        Assert.NotNull(wonDoc.QuerySelector(".chips .chip.won"));
        Assert.Contains("No activity yet.", wonDoc.QuerySelector(".empty")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Account_contact_and_activity_records_render_their_headers_tabs_and_facts()
    {
        var account = R(("Id", 3), ("Name", "Nordvind Rotor A/S"), ("IndustryLabel", "Wind"), ("IndustryCode", "wind"), ("Website", "https://nordvind.example"), ("City", "Esbjerg"), ("CountryCode", "DK"), ("OwnerId", 7), ("OwnerName", "Anna Holm"), ("TeamId", 3));
        var contactsTab = new TabTable("Contacts", ["Name"], [[new Cell("Lars Berg", "/contacts/5")]], "/contacts/new?accountId=3", "New contact");
        var model = DetailFor("accounts", account, ("SegmentCode", "wind"), ("Tabs", new List<TabInfo> { new("activity", "Activity", 0, "/accounts/3?tab=activity", false), new("contacts", "Contacts", 1, "/accounts/3?tab=contacts", true) }), ("TabTable", contactsTab),
            ("Facts", new List<Fact> { new("Open opportunities", "2"), new("Contacts", "1"), new("Last activity", "–") }), ("LogHref", "/activities/new?accountId=3"));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Entity/Detail", model, PageRenderHarness.User(), dumpSuffix: "account");
        Assert.NotNull(doc.QuerySelector(".page-header img.seg-icon"));
        Assert.NotNull(doc.QuerySelector(".page-meta .chip.segment.wind"));
        Assert.Equal("page", doc.QuerySelector(".tabs a[aria-current]")!.GetAttribute("aria-current"));
        Assert.Equal("New contact", doc.QuerySelector("#rel-h ~ p a, section p a.button")!.TextContent.Trim());
        Assert.Equal(3, doc.QuerySelectorAll(".facts > div").Length);

        var contact = R(("Id", 5), ("FirstName", "Lars"), ("LastName", "Berg"), ("AccountId", 3), ("AccountName", "Nordvind"), ("JobTitle", "CTO"), ("Email", "lars@example.test"), ("Phone", "+4512"), ("OwnerId", 7), ("OwnerName", "Anna Holm"), ("TeamId", 3));
        var cDoc = await PageRenderHarness.RenderAsync(_f, "/Entity/Detail", DetailFor("contacts", contact, ("LogHref", "/activities/new?contactId=5")), PageRenderHarness.User());
        Assert.NotNull(cDoc.QuerySelector(".page-header .avatar.s40"));
        Assert.Equal("mailto:lars@example.test", cDoc.QuerySelector(".contact-links a")!.GetAttribute("href"));

        var activity = R(("Id", 4), ("ActivityType", "call"), ("Subject", "Follow up"), ("Description", "Ring dem"), ("DueDate", "2026-11-17"), ("DueTime", "14:00"), ("IsDone", false), ("OwnerId", 7), ("OwnerName", "Anna Holm"), ("TeamId", 3), ("OpportunityId", 7), ("OpportunityName", "Blade"));
        var aDoc = await PageRenderHarness.RenderAsync(_f, "/Entity/Detail", DetailFor("activities", activity, ("DoneAction", new DoneAction("/activities/4/done", "Mark as done", "/activities/4"))), PageRenderHarness.User());
        Assert.Equal("Mark as done", aDoc.QuerySelector(".actions form button")!.TextContent);
        Assert.Contains("Ring dem", aDoc.QuerySelector("#desc-h")!.ParentElement!.TextContent, StringComparison.Ordinal);
        Assert.Contains("No changes recorded yet.", aDoc.QuerySelector(".empty")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Close_confirm_and_delete_pages_render()
    {
        var confirm = PageRenderHarness.Model<DetailModel>(("Def", EntityDefinitions.All["opportunities"]), ("Confirm", new ConfirmPage("Mark as won", "Mark Deal as won? The stage changes to Closed won. The close date will be set to today.", "/opportunities/7/won", [("rowVer", "AAAAAAAAB9E="), ("confirm", "1")], "Mark as won", "/opportunities/7", "Deal")));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Entity/Detail", confirm, PageRenderHarness.User());
        Assert.Contains("The close date will be set to today.", doc.QuerySelector("main p")!.TextContent, StringComparison.Ordinal);
        var del = PageRenderHarness.Model<DeleteModel>(("Def", EntityDefinitions.All["accounts"]), ("Id", 3), ("RecordName", "Nordvind"), ("Notice", "This also removes the link."));
        var dDoc = await PageRenderHarness.RenderAsync(_f, "/Entity/Delete", del, PageRenderHarness.User());
        Assert.Equal("Delete Nordvind?", dDoc.QuerySelector("h1")!.TextContent);
        Assert.Equal("Delete account", dDoc.QuerySelector("button.danger")!.TextContent);
        Assert.Equal("/accounts/3", dDoc.QuerySelector("a.button.quiet")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Pipeline_board_renders_columns_cards_move_forms_and_empty_states()
    {
        var options = new List<MoveOption> { new(1, "Qualification (current)", true), new(2, "Needs analysis", false) };
        var card = new DealCard(7, "Blade root seals 2027", "Nordvind Rotor A/S", 3, "EUR 240.000,00", "15.12.2026", true, "Anna Holm", "AAAAAAAAB9E=", true, options);
        var cols = new List<BoardColumn> { new(1, "Qualification", "10 %", 25, "6,0 mio. kr.", "DKK 6.000.000,00", "0,6 mio. kr.", 83, ["Uses latest rate for 1 currency"], [card], "/opportunities?status=open&stage=1", true), new(2, "Needs analysis", "25 %", 0, "0 kr.", "DKK 0,00", "0 kr.", 0, [], [], "/x", false) };
        var model = PageRenderHarness.Model<Crm.Web.Pages.Pipeline.IndexModel>(("Columns", cols), ("Filters", new List<FilterField> { new("owner", "Owner", [new SelectOption("", "All I can see"), new SelectOption("7", "Anna")], "") }),
            ("TotalShort", "24,1 mio. kr."), ("TotalFull", "DKK 24.100.000,00"), ("TotalCount", 25), ("Notes", new[] { "Uses latest rate for 1 currency" }));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Pipeline/Index", model, PageRenderHarness.User(), new Dictionary<string, object?> { ["ActiveNav"] = "pipeline" }, dumpSuffix: "board");
        Assert.Equal("Pipeline", doc.QuerySelector("h1")!.TextContent);
        Assert.Equal("Pipeline board", doc.QuerySelector(".board")!.GetAttribute("aria-label"));
        Assert.Equal("0", doc.QuerySelector(".board")!.GetAttribute("tabindex"));
        Assert.Equal("1", doc.QuerySelector("section.board-col")!.GetAttribute("data-stage"));
        Assert.Equal("opp-7", doc.QuerySelector("li.deal-card")!.Id);
        Assert.NotNull(doc.QuerySelector("a.deal-name[data-autofocus]"));
        Assert.Equal("/opportunities/7/stage", doc.QuerySelector("form.move-form")!.GetAttribute("action"));
        Assert.Equal("Move to Blade root seals 2027", doc.QuerySelector("form.move-form label")!.TextContent);
        Assert.Equal("Move Blade root seals 2027", doc.QuerySelector("form.move-form button")!.TextContent);
        Assert.Equal("Qualification (current)", doc.QuerySelector("form.move-form option[selected]")!.TextContent);
        Assert.NotNull(doc.QuerySelector("input[name=rowVer]"));
        Assert.Equal("Show all 25 in Qualification", doc.QuerySelector("a.show-all")!.TextContent);
        Assert.Contains("No opportunities in this stage.", doc.QuerySelectorAll(".board-col")[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Closed opportunities are in the list:", doc.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("(25)", doc.QuerySelector(".stage-jump")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("page", doc.QuerySelector("a.nav-item[href='/pipeline']")!.GetAttribute("aria-current"));
        var empty = PageRenderHarness.Model<Crm.Web.Pages.Pipeline.IndexModel>(("Columns", new List<BoardColumn>()), ("Filters", new List<FilterField> { new("owner", "Owner", [new SelectOption("", "All")], "") }), ("Notes", Array.Empty<string>()));
        var eDoc = await PageRenderHarness.RenderAsync(_f, "/Pipeline/Index", empty, PageRenderHarness.User());
        Assert.Contains("No open opportunities yet.", eDoc.QuerySelector(".empty")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stage_confirm_page_renders_through_the_shared_partial()
    {
        var model = PageRenderHarness.Model<StageModel>(("Confirm", new ConfirmPage("Mark as lost", "Mark Deal as lost? The stage changes to Closed lost (reason: Price).", "/opportunities/7/lost", [("rowVer", "x"), ("lostReason", "1"), ("confirm", "1")], "Mark as lost", "/opportunities/7", "Deal")));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Pipeline/Stage", model, PageRenderHarness.User());
        Assert.Equal("Mark as lost", doc.QuerySelector("h1")!.TextContent);
        Assert.Equal("/opportunities/7/lost", doc.QuerySelector("form[action$='/lost']")!.GetAttribute("action"));
    }

    [Fact]
    public async Task Search_page_renders_groups_hits_and_the_empty_states()
    {
        var groups = new List<SearchGroup>
        {
            new("accounts", "Accounts", "account", 7, [new SearchHit("Nordvind Rotor A/S", "/accounts/3", "Esbjerg · DK", "Anna Holm")], "/accounts?q=nord"),
            new("opportunities", "Opportunities", "opportunity", 1, [new SearchHit("Blade", "/opportunities/7", "Nordvind", "Anna Holm", "Proposal", "open", 3, 4)], "/opportunities?q=nord&status=all"),
            new("activities", "Activities", "activity", 1, [new SearchHit("Follow up", "/activities/4", "Call", "Anna Holm", Icon: "call")], "/activities?q=nord&status=all"),
        };
        var doc = await PageRenderHarness.RenderAsync(_f, "/Search", PageRenderHarness.Model<SearchModel>(("Text", "nord"), ("Groups", groups), ("TotalResults", 9)), PageRenderHarness.User(), dumpSuffix: "results");
        Assert.Equal("Results for “nord”", doc.QuerySelector("main h2")!.TextContent);
        Assert.Equal("9 results found", doc.QuerySelector("p[role=status]")!.TextContent);
        Assert.Equal("Accounts (7)", doc.QuerySelector("#rg-accounts")!.TextContent);
        Assert.Equal("See all 7 accounts", doc.QuerySelector("section[aria-labelledby=rg-accounts] .panel-head a")!.TextContent);
        Assert.NotNull(doc.QuerySelector(".result-list .chip.stage"));
        Assert.Equal("nord", doc.QuerySelector("#search-q")!.GetAttribute("value"));
        var none = await PageRenderHarness.RenderAsync(_f, "/Search", PageRenderHarness.Model<SearchModel>(("Text", "xyz"), ("Groups", new List<SearchGroup>())), PageRenderHarness.User());
        Assert.Contains("No records match “xyz”.", none.QuerySelector(".empty")!.TextContent, StringComparison.Ordinal);
        var blank = await PageRenderHarness.RenderAsync(_f, "/Search", PageRenderHarness.Model<SearchModel>(), PageRenderHarness.User());
        Assert.Contains("Search names, accounts, emails and subjects.", blank.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signed_out_pages_use_the_brand_layout_and_error_pages_render_inside_the_shell_or_bare()
    {
        var signin = PageRenderHarness.Model<SigninModel>(("Expired", true), ("ReturnTo", "/accounts"));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Signin", signin);
        Assert.Contains("Your session has ended.", doc.QuerySelector(".alert")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("/auth/login?returnTo=%2Faccounts", doc.QuerySelector("a.button.primary")!.GetAttribute("href"));
        var notFound = PageRenderHarness.Model<ErrorModel>(("Heading", "We can't find that page or record."), ("Message", "It may have been deleted, or you may not have access."), ("Reference", "abc"), ("Status", 404), ("PageTitle", "Not found"));
        var shell = await PageRenderHarness.RenderAsync(_f, "/Errors/Error", notFound, PageRenderHarness.User(), dumpSuffix: "404");
        Assert.NotNull(shell.QuerySelector("nav.sidebar"));
        Assert.Equal("We can't find that page or record.", shell.QuerySelector("h1")!.TextContent);
        Assert.Equal("Search", shell.QuerySelector(".actions a.secondary")!.TextContent);
        Assert.Contains("Reference: abc", shell.QuerySelector(".system-block p.muted")!.TextContent, StringComparison.Ordinal);
        var server = PageRenderHarness.Model<ErrorModel>(("Heading", "Something went wrong on our side."), ("Message", "Try again in a moment."), ("Reference", "abc"), ("Status", 500), ("PageTitle", "Error"));
        var sDoc = await PageRenderHarness.RenderAsync(_f, "/Errors/Error", server, PageRenderHarness.User());
        Assert.Equal("Try again", sDoc.QuerySelector(".actions a.primary")!.TextContent);
        var waking = PageRenderHarness.Model<ErrorModel>(("Kind", ErrorKind.Waking), ("PageTitle", "Waking up the database"), ("Reference", "abc"));
        var wDoc = await PageRenderHarness.RenderAsync(_f, "/Errors/Error", waking, PageRenderHarness.User(), dumpSuffix: "waking");
        Assert.Null(wDoc.QuerySelector("nav.sidebar")); // the waking page renders without the app shell
        Assert.Equal("Waking up the database", wDoc.QuerySelector("h1")!.TextContent);
        Assert.NotNull(wDoc.QuerySelector("#wake-retry"));
        Assert.True(wDoc.QuerySelector("#wake-giveup")!.HasAttribute("hidden"));
        var conflict = PageRenderHarness.Model<ErrorModel>(("Kind", ErrorKind.Conflict), ("RecordPath", "/opportunities/7"), ("Reference", "abc"));
        var cDoc = await PageRenderHarness.RenderAsync(_f, "/Errors/Error", conflict, PageRenderHarness.User());
        Assert.Equal("Someone else changed this record", cDoc.QuerySelector("h1")!.TextContent);
        Assert.Equal("/opportunities/7", cDoc.QuerySelector("a.button.primary")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Settings_pages_use_the_settings_layout_with_nav_brand_preview_and_radio_cards()
    {
        var general = PageRenderHarness.Model<Crm.Web.Pages.Settings.GeneralModel>(("AppName", "CRM"), ("AccentColor", "#1A66CC"), ("Errors", new Dictionary<string, string> { ["accentColor"] = "This colour is too light for text and buttons. Choose a darker shade." }), ("SettingsNav", "general"));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Settings/General", general, PageRenderHarness.User(CrmRole.Admin), new Dictionary<string, object?> { ["SettingsNav"] = "general", ["ActiveNav"] = "settings" }, dumpSuffix: "general");
        Assert.Equal("page", doc.QuerySelector("nav.subnav a[href='/settings/general']")!.GetAttribute("aria-current"));
        Assert.Equal(7, doc.QuerySelectorAll("nav.subnav a").Length);
        Assert.NotNull(doc.QuerySelector(".settings-layout .settings-content"));
        Assert.Contains("The Dark theme always uses its own brighter soft blue.", doc.QuerySelector("#h-accentColor")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("#EAF2FD", doc.QuerySelector("#h-accentColor")!.TextContent, StringComparison.Ordinal);
        Assert.Equal(2, doc.QuerySelectorAll(".preview-sample").Length);
        Assert.NotNull(doc.QuerySelector(".swatch"));
        Assert.Contains("too light", doc.QuerySelector("#e-accentColor")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("page", doc.QuerySelector("a.nav-item[href='/settings/general']")!.GetAttribute("aria-current"));
        var vis = PageRenderHarness.Model<Crm.Web.Pages.Settings.VisibilityModel>(("Mode", "team"));
        var vDoc = await PageRenderHarness.RenderAsync(_f, "/Settings/Visibility", vis, PageRenderHarness.User(CrmRole.Admin), new Dictionary<string, object?> { ["SettingsNav"] = "visibility" });
        Assert.Equal(2, vDoc.QuerySelectorAll("label.radio-card").Length);
        Assert.NotNull(vDoc.QuerySelector("label.radio-card input[value=team][checked]"));
        var stages = PageRenderHarness.Model<Crm.Web.Pages.Settings.StagesModel>(("Rows", new List<Crm.Web.Pages.Settings.StageRowVm> { new("1", "Qualification", "10", "open", 0), new("5", "Closed won", "100", "won", 40) }), ("Version", "v"), ("Note", "Changes are not saved yet. Choose Save stages to keep them."));
        var sDoc = await PageRenderHarness.RenderAsync(_f, "/Settings/Stages", stages, PageRenderHarness.User(CrmRole.Admin), new Dictionary<string, object?> { ["SettingsNav"] = "stages" });
        Assert.Equal("disabled", sDoc.QuerySelector("#up-0")!.GetAttribute("disabled"));
        Assert.NotNull(sDoc.QuerySelector("#up-1 svg"));
        Assert.NotNull(sDoc.QuerySelector("p.flash-warn[role=status]"));
        Assert.Contains("(locked: 40 opportunities)", sDoc.QuerySelector("#f-type1")!.TextContent, StringComparison.Ordinal);
    }
}
