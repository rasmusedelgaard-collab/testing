using AngleSharp.Dom;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Pages;
using Crm.Web.Pages.Dashboard;
using Crm.Web.Pages.Entity;
using Crm.Web.Pages.Pipeline;
using Crm.Web.Services;

namespace Crm.Tests.Http;

/// <summary>User-entered names are HTML-encoded wherever the redesigned pages print them (opportunity, account and stage names, activity subjects).</summary>
public sealed class HostileNameRenderTests : IDisposable
{
    private const string Hostile = "\"><img src=x onerror=alert(1)>";
    private static readonly Formatter F = new("da-DK", "Europe/Copenhagen");
    private readonly CrmFactory _f = new();

    public void Dispose() => _f.Dispose();

    private static Row R(params (string, object?)[] kv) => new(kv.ToDictionary(x => x.Item1, x => x.Item2));

    private static void NothingInjected(IDocument doc)
    {
        Assert.Empty(doc.QuerySelectorAll("img[src=x]"));
        Assert.Empty(doc.QuerySelectorAll("[onerror]"));
    }

    private static void Shows(IDocument doc, string selector)
    {
        var els = doc.QuerySelectorAll(selector);
        Assert.NotEmpty(els);
        Assert.All(els, e => Assert.Contains(Hostile, e.TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dashboard_encodes_hostile_names_in_opp_list_tile_caption_legend_and_activities()
    {
        var activity = TimelineBuilder.ActivityItem(R(("Id", 1), ("ActivityType", "call"), ("Subject", Hostile), ("Description", Hostile), ("DueDate", "2026-11-18"), ("DueTime", null), ("IsDone", false), ("OwnerName", Hostile),
            ("OpportunityId", 3), ("OpportunityName", Hostile), ("AccountId", 2), ("AccountName", Hostile), ("CreatedAt", "2026-11-01T08:00:00.000Z")), F, "2026-11-19", "/");
        var tiles = new List<TileVm> { new(Hostile, "/opportunities", "1 mio", "1.000.000 kr.", Hostile, [], false, "", false, false, Stack: [new TileSeg(Hostile, 1, 60), new TileSeg(Hostile + "2", 2, 40)]) };
        var opps = new List<OppRowVm> { new(3, Hostile, 2, Hostile, Hostile, "open", 1, 4, "1 mio", "1.000.000 kr.", "20.11.2026", false) };
        var model = PageRenderHarness.Model<Crm.Web.Pages.Dashboard.IndexModel>(("Greeting", "Good morning"), ("AsOf", "19.11.2026 09:14"), ("LongDate", "Thursday 19 November"), ("FirstName", "Lars"), ("Tiles", tiles), ("UserId", 7), ("BaseCurrency", "DKK"),
            ("OppList", opps), ("OppTotal", 1), ("Upcoming", new List<UpcomingGroup> { new("Overdue", 1, [activity], "/activities?due=overdue", "View all") }),
            ("Funnel", new List<FunnelRow>()), ("FunnelNotes", Array.Empty<string>()), ("Summary", ""), ("ViewAllDueHref", "/activities"));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Dashboard/Index", model, PageRenderHarness.User(), new Dictionary<string, object?> { ["ActiveNav"] = "dashboard", ["Title"] = "Dashboard" });
        NothingInjected(doc);
        Shows(doc, ".opp-name");
        Shows(doc, ".opp-sub");
        Shows(doc, ".tile-context");
        Assert.Contains(Hostile, doc.QuerySelector(".stack-legend .lg")!.GetAttribute("title"), StringComparison.Ordinal);
        Assert.Equal("\"><img", doc.QuerySelector(".stack-legend .lg")!.TextContent);
        Shows(doc, ".my-day .tl-title a");
    }

    [Fact]
    public async Task Board_encodes_hostile_deal_card_and_stage_names()
    {
        var card = new DealCard(3, Hostile, Hostile, 2, "1.000 kr.", "1 t", "20.11.2026", false, Hostile, "AAAAAAAAB9E=", false, [new MoveOption(1, Hostile, true)]);
        var col = new BoardColumn(1, Hostile, "10 %", 1, "1 t", "1.000 kr.", "100", 100, [], [card], "/opportunities?stage=1", false);
        var model = PageRenderHarness.Model<Crm.Web.Pages.Pipeline.IndexModel>(("Columns", new List<BoardColumn> { col }), ("Filters", new List<FilterField> { new("owner", "Owner", [new SelectOption("", "All")], "") }), ("TotalShort", "1 t"), ("TotalFull", "1.000 kr."), ("TotalCount", 1), ("Notes", Array.Empty<string>()));
        var doc = await PageRenderHarness.RenderAsync(_f, "/Pipeline/Index", model, PageRenderHarness.User(), new Dictionary<string, object?> { ["ActiveNav"] = "pipeline", ["Title"] = "Pipeline" });
        NothingInjected(doc);
        Shows(doc, ".deal-card .deal-name");
        Shows(doc, ".deal-card .deal-account a");
        Shows(doc, ".board-col .col-name");
    }

    [Fact]
    public async Task Search_encodes_hostile_hits()
    {
        var hits = new List<SearchHit> { new(Hostile, "/opportunities/3", Hostile, Hostile, Hostile, "open", 1, 4, "opportunities") };
        var groups = new List<SearchGroup> { new("opportunities", "Opportunities", "opportunity", 1, hits, "/opportunities?q=x") };
        var doc = await PageRenderHarness.RenderAsync(_f, "/Search", PageRenderHarness.Model<SearchModel>(("Text", Hostile), ("Groups", groups), ("TotalResults", 1)), PageRenderHarness.User(), new Dictionary<string, object?> { ["ActiveNav"] = "" });
        NothingInjected(doc);
        Assert.Contains(Hostile, doc.QuerySelector(".result-list")!.TextContent, StringComparison.Ordinal);
    }
}
