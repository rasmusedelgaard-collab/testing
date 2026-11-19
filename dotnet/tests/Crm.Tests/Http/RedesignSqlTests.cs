using System.Net;
using Crm.Web.Data;
using Microsoft.Data.SqlClient;

namespace Crm.Tests.Http;

/// <summary>Board, stage move, search, timeline and My day over real SQL with the real sign-in flow and RLS.</summary>
[Collection("sql")]
public sealed class RedesignSqlTests(SqlServerFixture fx)
{
    private const string UserA1 = "00000000-0000-4000-8000-00000000a003";
    private const string UserB1 = "00000000-0000-4000-8000-00000000b002";

    private static string I(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Unique(string p) => p + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<int> UserIdAsync(string oid)
    {
        await using var c = new SqlConnection(SqlConnectionFactory.Build(fx.OwnerDb, "owner-redesign", 1, false, 30));
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT UserId FROM crm.Users WHERE EntraOid = @o", c);
        cmd.Parameters.AddWithValue("@o", Guid.Parse(oid));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(CrmFactory F, HttpClient C)> AsAsync(string oid)
    {
        var f = new CrmFactory(fx.AppDb);
        var c = HttpHelpers.NewClient(f);
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await HttpHelpers.SignInAsync(c, oid));
        return (f, c);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string formPath, string postPath, params (string, string)[] fields) =>
        await c.PostAsync(postPath, HttpHelpers.Form([("_csrf", await HttpHelpers.TokenAsync(c, formPath)), .. fields]));

    private sealed record Seed(int AccountId, int OppId, string OppName, string OppPath, string OpenStage, string WonStage, string LostStage);

    private async Task<Seed> SeedOpportunityAsync(HttpClient c, string oid)
    {
        var me = await UserIdAsync(oid);
        var acc = await PostAsync(c, "/accounts/new", "/accounts", ("Name", Unique("Board")), ("OwnerId", I(me)));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, acc);
        var accId = int.Parse(acc.Headers.Location!.OriginalString.Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture);
        var form = await HttpHelpers.ParseAsync(await c.GetAsync("/opportunities/new"));
        var options = form.QuerySelectorAll("#f-StageId option").ToList();
        string StageLike(string text) => options.First(o => o.TextContent.Contains(text, StringComparison.Ordinal)).GetAttribute("value")!;
        var open = options.First().GetAttribute("value")!;
        var name = Unique("Deal");
        var created = await PostAsync(c, "/opportunities/new", "/opportunities", ("Name", name), ("AccountId", I(accId)), ("StageId", open), ("Amount", "1.000,00"), ("CurrencyCode", "DKK"), ("CloseDate", "2099-06-01"), ("OwnerId", I(me)));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, created);
        var path = created.Headers.Location!.OriginalString;
        return new Seed(accId, int.Parse(path.Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture), name, path, open, StageLike("Closed won"), StageLike("Closed lost"));
    }

    private static AngleSharp.Dom.IElement Card(AngleSharp.Html.Dom.IHtmlDocument doc, string name) =>
        doc.QuerySelectorAll("li.deal-card").First(li => li.QuerySelector("a.deal-name")!.TextContent == name);

    [SqlFact]
    public async Task Board_shows_open_opportunities_and_a_move_updates_the_stage_audits_and_refuses_a_stale_rowver()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var seed = await SeedOpportunityAsync(c, UserA1);
        var board = await HttpHelpers.ParseAsync(await c.GetAsync("/pipeline"));
        Assert.Equal("Pipeline", board.QuerySelector("h1")!.TextContent);
        Assert.Equal("page", board.QuerySelector("a.nav-item[href='/pipeline']")!.GetAttribute("aria-current"));
        Assert.Equal("Pipeline board", board.QuerySelector(".board")!.GetAttribute("aria-label"));
        var card = Card(board, seed.OppName);
        Assert.Equal(seed.OpenStage, card.Closest("section.board-col")!.GetAttribute("data-stage"));
        var form = card.QuerySelector("form.move-form")!;
        Assert.Equal($"/opportunities/{seed.OppId}/stage", form.GetAttribute("action"));
        var select = form.QuerySelector("select")!;
        Assert.EndsWith("(current)", select.QuerySelector("option[selected]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains(seed.OppName, select.ParentElement!.QuerySelector("label")!.TextContent, StringComparison.Ordinal);
        var target = select.QuerySelectorAll("option").First(o => !o.HasAttribute("selected"));
        var rowVer = form.QuerySelector("input[name=rowVer]")!.GetAttribute("value")!;
        var token = board.QuerySelector("input[name=_csrf]")!.GetAttribute("value")!;

        var moved = await c.PostAsync($"/opportunities/{seed.OppId}/stage", HttpHelpers.Form(("_csrf", token), ("rowVer", rowVer), ("stageId", target.GetAttribute("value")!), ("returnTo", "/pipeline")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, moved);
        Assert.Equal($"/pipeline#opp-{seed.OppId}", moved.Headers.Location!.OriginalString);
        var after = await HttpHelpers.ParseAsync(await c.GetAsync("/pipeline"));
        Assert.Equal($"Moved {seed.OppName} to {target.TextContent}.", after.QuerySelector(".flash")!.TextContent);
        Assert.Equal(target.GetAttribute("value"), Card(after, seed.OppName).Closest("section.board-col")!.GetAttribute("data-stage"));
        Assert.NotNull(Card(after, seed.OppName).QuerySelector("a.deal-name[data-autofocus]"));
        var detail = await HttpHelpers.ParseAsync(await c.GetAsync(seed.OppPath));
        Assert.Contains("Stage changed from", detail.QuerySelector("ol.timeline")!.TextContent, StringComparison.Ordinal);

        var stale = await c.PostAsync($"/opportunities/{seed.OppId}/stage", HttpHelpers.Form(("_csrf", token), ("rowVer", rowVer), ("stageId", seed.OpenStage)));
        await HttpHelpers.ExpectAsync(HttpStatusCode.Conflict, stale);
    }

    [SqlFact]
    public async Task Moving_to_won_or_lost_uses_the_same_confirm_and_reason_rules_as_the_record()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var seed = await SeedOpportunityAsync(c, UserA1);
        var board = await HttpHelpers.ParseAsync(await c.GetAsync("/pipeline"));
        var rowVer = Card(board, seed.OppName).QuerySelector("input[name=rowVer]")!.GetAttribute("value")!;
        var token = board.QuerySelector("input[name=_csrf]")!.GetAttribute("value")!;

        var won = await c.PostAsync($"/opportunities/{seed.OppId}/stage", HttpHelpers.Form(("_csrf", token), ("rowVer", rowVer), ("stageId", seed.WonStage)));
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, won);
        var confirm = await HttpHelpers.ParseAsync(won);
        Assert.Equal("Mark as won", confirm.QuerySelector("h1")!.TextContent);
        Assert.Contains("The close date will be set to today.", confirm.QuerySelector("main p")!.TextContent, StringComparison.Ordinal);
        Assert.Equal($"/opportunities/{seed.OppId}/won", confirm.QuerySelector("form[action$='/won']")!.GetAttribute("action"));
        Assert.Equal("1", confirm.QuerySelector("input[name=confirm]")!.GetAttribute("value"));
        var still = await HttpHelpers.ParseAsync(await c.GetAsync(seed.OppPath));
        Assert.NotNull(still.QuerySelector("#stage-h")); // not closed by the first POST

        var lost = await c.PostAsync($"/opportunities/{seed.OppId}/stage", HttpHelpers.Form(("_csrf", token), ("rowVer", rowVer), ("stageId", seed.LostStage), ("lostReason", "")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, lost);
        Assert.Equal(seed.OppPath, lost.Headers.Location!.OriginalString);
        var back = await HttpHelpers.ParseAsync(await c.GetAsync(seed.OppPath));
        Assert.Contains("Choose why this was lost", back.QuerySelector(".flash")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(back.QuerySelector("#stage-h"));

        var bad = await c.PostAsync($"/opportunities/{seed.OppId}/stage", HttpHelpers.Form(("_csrf", token), ("rowVer", rowVer), ("stageId", "999999")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, bad);
        Assert.Equal("/pipeline", bad.Headers.Location!.OriginalString);
    }

    [SqlFact]
    public async Task Stage_endpoint_respects_rls_antiforgery_and_the_crmid_constraint()
    {
        var (fa, a) = await AsAsync(UserA1);
        using var _a = fa;
        var seed = await SeedOpportunityAsync(a, UserA1);
        var board = await HttpHelpers.ParseAsync(await a.GetAsync("/pipeline"));
        var rowVer = Card(board, seed.OppName).QuerySelector("input[name=rowVer]")!.GetAttribute("value")!;
        var other = Card(board, seed.OppName).QuerySelectorAll("option").First(o => !o.HasAttribute("selected")).GetAttribute("value")!;

        var (fb, b) = await AsAsync(UserB1);
        using var _b = fb;
        var tokenB = await HttpHelpers.TokenAsync(b, "/pipeline");
        // another team cannot see the record, so the move answers like a missing record
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await b.PostAsync($"/opportunities/{seed.OppId}/stage", HttpHelpers.Form(("_csrf", tokenB), ("rowVer", rowVer), ("stageId", other))));
        var theirBoard = await HttpHelpers.ParseAsync(await b.GetAsync("/pipeline"));
        Assert.DoesNotContain(theirBoard.QuerySelectorAll("a.deal-name"), x => x.TextContent == seed.OppName);

        await HttpHelpers.ExpectAsync(HttpStatusCode.Forbidden, await a.PostAsync($"/opportunities/{seed.OppId}/stage", HttpHelpers.Form(("rowVer", rowVer), ("stageId", other))));
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await a.PostAsync("/opportunities/abc/stage", HttpHelpers.Form(("_csrf", await HttpHelpers.TokenAsync(a, "/pipeline")), ("stageId", other))));
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await a.GetAsync($"/opportunities/{seed.OppId}/stage"));
        var unchanged = await HttpHelpers.ParseAsync(await a.GetAsync("/pipeline"));
        Assert.Equal(seed.OpenStage, Card(unchanged, seed.OppName).Closest("section.board-col")!.GetAttribute("data-stage"));
    }

    [SqlFact]
    public async Task Search_groups_results_per_entity_applies_rls_and_has_the_empty_states()
    {
        var (fa, a) = await AsAsync(UserA1);
        using var _a = fa;
        var tag = Guid.NewGuid().ToString("N")[..10];
        var me = await UserIdAsync(UserA1);
        var acc = await PostAsync(a, "/accounts/new", "/accounts", ("Name", "Zorb " + tag), ("OwnerId", I(me)), ("City", "Esbjerg"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, acc);
        var doc = await HttpHelpers.ParseAsync(await a.GetAsync("/search?q=" + tag));
        Assert.Equal("Search", doc.QuerySelector("h1")!.TextContent);
        Assert.Equal("Accounts (1)", doc.QuerySelector("#rg-accounts")!.TextContent);
        Assert.Equal(acc.Headers.Location!.OriginalString, doc.QuerySelector(".result-list a")!.GetAttribute("href"));
        Assert.Contains("1 result found", doc.QuerySelector("p.muted[role=status]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains($"/accounts?q={tag}", doc.QuerySelector(".result-group a[href*='/accounts?q=']")!.GetAttribute("href"), StringComparison.Ordinal);
        Assert.Null(doc.QuerySelector("#rg-contacts"));

        var (fb, b) = await AsAsync(UserB1);
        using var _b = fb;
        var theirs = await HttpHelpers.ParseAsync(await b.GetAsync("/search?q=" + tag));
        Assert.Contains($"No records match “{tag}”.", theirs.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("0 results found", theirs.QuerySelector("p.muted[role=status]")!.TextContent, StringComparison.Ordinal);

        var empty = await HttpHelpers.ParseAsync(await a.GetAsync("/search"));
        Assert.Contains("Search names, accounts, emails and subjects.", empty.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/search?q=" + new string('x', 300))).StatusCode);
    }

    [SqlFact]
    public async Task Record_timeline_merges_activities_and_history_and_my_day_lists_the_same_overdue_work()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var me = await UserIdAsync(UserA1);
        var seed = await SeedOpportunityAsync(c, UserA1);
        var subject = Unique("Follow up");
        var act = await PostAsync(c, "/activities/new", "/activities", ("ActivityType", "task"), ("Subject", subject), ("OpportunityId", I(seed.OppId)), ("DueDate", "2020-01-02"), ("OwnerId", I(me)));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, act);

        var detail = await HttpHelpers.ParseAsync(await c.GetAsync(seed.OppPath));
        var timeline = detail.QuerySelector("ol.timeline")!;
        var groups = timeline.Children.Where(li => li.ClassList.Contains("timeline-group")).Select(li => li.QuerySelector("h3")!.TextContent).ToList();
        Assert.Equal("Overdue", groups[0]);
        Assert.Contains(groups, g => g.Length > 5 && g.Contains("20", StringComparison.Ordinal)); // a month group with the creation history
        Assert.Contains($"Task: {subject}", timeline.TextContent, StringComparison.Ordinal);
        Assert.Contains("overdue", timeline.QuerySelector(".chip.danger")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Created", timeline.TextContent, StringComparison.Ordinal);
        Assert.Contains("/done", timeline.QuerySelector("form.tl-action")!.GetAttribute("action"), StringComparison.Ordinal);
        Assert.StartsWith("Activity (", detail.QuerySelector("h2")!.TextContent, StringComparison.Ordinal);
        Assert.Contains(seed.OppName, detail.QuerySelector("h1")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(detail.QuerySelector("ol.stage-track li[aria-current=step]"));
        Assert.Equal($"/activities/new?opportunityId={seed.OppId}", detail.QuerySelector(".page-header a.button.primary")!.GetAttribute("href"));

        var dash = await HttpHelpers.ParseAsync(await c.GetAsync("/"));
        Assert.Equal("My day", dash.QuerySelector("#upcoming-h")!.TextContent);
        var myDay = dash.QuerySelector("section[aria-labelledby=upcoming-h]")!;
        Assert.Contains("Overdue (", myDay.QuerySelector("h3")!.TextContent, StringComparison.Ordinal);
        Assert.Contains(subject, myDay.TextContent, StringComparison.Ordinal);
        Assert.Contains("View all overdue activities", myDay.TextContent, StringComparison.Ordinal);
        var tiles = dash.QuerySelectorAll("li.tile").ToList();
        Assert.NotNull(tiles[2].QuerySelector(".tile-trend")); // trend on Won this month
        Assert.NotNull(tiles[3].QuerySelector("svg.spark")); // sparkline on Won this quarter
        Assert.Null(tiles[0].QuerySelector(".tile-trend")); // none for open pipeline (D7)
        Assert.Contains("Open pipeline by stage", dash.QuerySelector("#funnel-h")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("Open board", dash.QuerySelector("section[aria-labelledby=funnel-h] a[href='/pipeline']")!.TextContent);
    }

    [SqlFact]
    public async Task Lists_show_chips_quick_actions_and_active_filter_chips()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var seed = await SeedOpportunityAsync(c, UserA1);
        var doc = await HttpHelpers.ParseAsync(await c.GetAsync("/opportunities?stage=" + seed.OpenStage));
        var row = doc.QuerySelectorAll("table.data tbody tr").First(r => r.TextContent.Contains(seed.OppName, StringComparison.Ordinal));
        Assert.NotNull(row.QuerySelector(".chip.stage"));
        Assert.Equal($"Edit {seed.OppName}", row.QuerySelector("td.act a")!.GetAttribute("aria-label"));
        Assert.StartsWith("Remove filter Stage:", doc.QuerySelector(".filter-chips a.chip")!.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Equal("/pipeline", doc.QuerySelector(".view-switch a:nth-child(2)")!.GetAttribute("href"));
        Assert.DoesNotContain("☐", doc.Body!.TextContent, StringComparison.Ordinal);
        var acts = await HttpHelpers.ParseAsync(await c.GetAsync("/activities?status=all"));
        Assert.DoesNotContain("☐", acts.Body!.TextContent, StringComparison.Ordinal);
    }
}
