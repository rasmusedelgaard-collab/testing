using System.Net;
using System.Text.Json;
using Crm.Web.Data;
using Microsoft.Data.SqlClient;

namespace Crm.Tests.Http;

[Collection("sql")]
public sealed class DashboardTests(SqlServerFixture fx)
{
    private const string UserB1 = "00000000-0000-4000-8000-00000000b002";

    private async Task<int> UserIdAsync(string oid)
    {
        await using var c = new SqlConnection(SqlConnectionFactory.Build(fx.OwnerDb, "owner-dash", 1, false, 30));
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

    private static string I(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [SqlFact]
    public async Task Dashboard_shows_six_tiles_with_links_notes_for_missing_fx_and_my_lists()
    {
        var (f, c) = await AsAsync(UserB1);
        using var guard = f;
        var me = await UserIdAsync(UserB1);
        var acc = await PostAsync(c, "/accounts/new", "/accounts", ("Name", "Dash-" + Guid.NewGuid().ToString("N")[..6]), ("OwnerId", I(me)));
        var accId = acc.Headers.Location!.OriginalString.Split('/')[^1];
        var form = await HttpHelpers.ParseAsync(await c.GetAsync("/opportunities/new"));
        var stage = form.QuerySelectorAll("#f-StageId option").First().GetAttribute("value")!;
        foreach (var (cur, amt) in new[] { ("DKK", "100.000,00"), ("SEK", "5.000,00") })
        {
            await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, "/opportunities/new", "/opportunities", ("Name", "D-" + cur), ("AccountId", accId), ("StageId", stage), ("Amount", amt), ("CurrencyCode", cur), ("CloseDate", "2099-01-01"), ("OwnerId", I(me))));
        }
        var page = await c.GetAsync("/");
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, page);
        var doc = await HttpHelpers.ParseAsync(page);
        var tiles = doc.QuerySelectorAll("li.tile").ToList();
        Assert.Equal(["Open pipeline", "Weighted pipeline", "Won this month", "Won this quarter", "Win rate (last 90 d)", "My activities due"], tiles.Select(t => t.QuerySelector(".tile-label")!.TextContent));
        var open = tiles[0];
        Assert.Contains("100 t kr.", open.QuerySelector(".tile-value")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("(DKK 100.000,00)", open.QuerySelector(".tile-value")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 opportunity in SEK not included: no FX rate", open.QuerySelector(".tile-note")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Ask an administrator.", open.QuerySelector(".tile-note")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("/opportunities?status=open", open.QuerySelector("a.tile-link")!.GetAttribute("href"));
        Assert.Contains("No closed deals yet", tiles[4].QuerySelector(".tile-value")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("My open opportunities", doc.QuerySelector("#my-opps-h")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("D-DKK", doc.QuerySelector("section[aria-labelledby=my-opps-h] .opp-list")!.TextContent, StringComparison.Ordinal);
        Assert.Matches("^Good (morning|afternoon|evening), Test$", doc.QuerySelector("h1")!.TextContent);
        foreach (var href in tiles.Select(t => t.QuerySelector("a.tile-link")!.GetAttribute("href")!))
        {
            await HttpHelpers.ExpectAsync(HttpStatusCode.OK, await c.GetAsync(href));
        }
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(doc.QuerySelector("section[aria-labelledby=my-opps-h] p a")!.GetAttribute("href")!)).StatusCode);
    }

    [SqlFact]
    public async Task Admin_gets_the_add_fx_rates_link()
    {
        var (f, c) = await AsAsync("00000000-0000-4000-8000-00000000a001");
        using var guard = f;
        var doc = await HttpHelpers.ParseAsync(await c.GetAsync("/"));
        Assert.Equal(6, doc.QuerySelectorAll("li.tile").Length);
    }

    [SqlFact]
    public async Task Analytics_renders_five_figures_with_json_and_table_fallbacks_and_validates_custom_ranges()
    {
        var (f, c) = await AsAsync("00000000-0000-4000-8000-00000000a001");
        using var guard = f;
        var page = await c.GetAsync("/analytics");
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, page);
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
        var doc = await HttpHelpers.ParseAsync(page);
        Assert.Equal(5, doc.QuerySelectorAll("figure.chart").Length);
        Assert.Equal(5, doc.QuerySelectorAll("figure.chart details.chart-table table").Length);
        Assert.Empty(doc.QuerySelectorAll("script:not([src]):not([type='application/json'])"));
        Assert.NotNull(doc.QuerySelector("script[src='/vendor/chart.umd.js']"));
        foreach (var script in doc.QuerySelectorAll("script[type='application/json']"))
        {
            using var json = JsonDocument.Parse(script.TextContent);
            Assert.True(json.RootElement.TryGetProperty("labels", out _));
        }
        var bad = await HttpHelpers.ParseAsync(await c.GetAsync("/analytics?period=custom&from=2026-02-01&to=2026-01-01"));
        Assert.Contains("End date must be on or after start date. Showing this quarter instead.", bad.QuerySelector("#e-range")!.TextContent, StringComparison.Ordinal);
        foreach (var url in new[] { "/analytics?owner=team:1", "/analytics?owner=abc", "/analytics?owner=99999999999", "/analytics?period=zzz", "/analytics?period=custom&from=x&to=y" })
        {
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(url)).StatusCode);
        }
    }

    [SqlFact]
    public async Task A_user_sees_no_team_options_and_dashboard_requires_sign_in()
    {
        var (f, c) = await AsAsync("00000000-0000-4000-8000-00000000a003");
        using var guard = f;
        var doc = await HttpHelpers.ParseAsync(await c.GetAsync("/analytics"));
        Assert.DoesNotContain(doc.QuerySelectorAll("#f-owner option"), o => o.GetAttribute("value")!.StartsWith("team:", StringComparison.Ordinal));
        using var anon = new CrmFactory(fx.AppDb);
        Assert.Equal(HttpStatusCode.SeeOther, (await HttpHelpers.NewClient(anon).GetAsync("/analytics")).StatusCode);
    }
}
