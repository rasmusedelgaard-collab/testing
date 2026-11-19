using System.Globalization;
using System.Net;
using AngleSharp.Html.Dom;
using Crm.Web.Data;
using Microsoft.Data.SqlClient;

namespace Crm.Tests.Http;

/// <summary>Increment 4: currency and locale, FX rates, pipeline stages and picklists (ports of settingsHttp.test.ts), over real SQL.</summary>
[Collection("sql")]
public sealed class SettingsConfigTests(SqlServerFixture fx)
{
    private const string Admin = "00000000-0000-4000-8000-00000000a001";
    private const string UserA1 = "00000000-0000-4000-8000-00000000a003";

    private async Task<(CrmFactory F, HttpClient C)> AsAsync(string oid)
    {
        var f = new CrmFactory(fx.AppDb);
        var c = HttpHelpers.NewClient(f);
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await HttpHelpers.SignInAsync(c, oid));
        return (f, c);
    }

    private async Task<List<(string Action, string? Field, string? Old, string? New)>> AuditAsync(string type, int id)
    {
        await using var c = new SqlConnection(SqlConnectionFactory.Build(fx.OwnerDb, "owner-cfg", 1, false, 30));
        await c.OpenAsync();
        // The owner login is subject to RLS too: read the audit log as the Admin context (as the seed script does).
        await SqlServerFixture.ExecAsync(c, "EXEC sp_set_session_context @key = N'UserId', @value = 0; EXEC sp_set_session_context @key = N'Role', @value = N'Admin';");
        await using var cmd = new SqlCommand("SELECT Action, FieldName, OldValue, NewValue FROM crm.AuditLog WHERE EntityType = @t AND EntityId = @i ORDER BY AuditId", c);
        cmd.Parameters.AddWithValue("@t", type);
        cmd.Parameters.AddWithValue("@i", id);
        var list = new List<(string, string?, string?, string?)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add((r.GetString(0).Trim(), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return list;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string formPath, string postPath, params (string, string)[] fields) =>
        await c.PostAsync(postPath, HttpHelpers.Form([("_csrf", await HttpHelpers.TokenAsync(c, formPath)), .. fields]));

    private static string Unique(string p) => p + "-" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<string> TextAsync(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    // ---------------- access control ----------------

    private static readonly string[] GetPaths =
    [
        "/settings", "/settings/general", "/settings/general/logo", "/settings/locale", "/settings/fx", "/settings/fx/1/edit", "/settings/fx/1/delete", "/settings/stages",
        "/settings/picklists", "/settings/picklists/industry", "/settings/visibility", "/settings/users", "/settings/teams", "/settings/teams/1", "/settings/teams/1/edit", "/settings/teams/1/deactivate",
    ];

    private static readonly string[] PostPaths =
    [
        "/settings/general", "/settings/general/logo", "/settings/locale", "/settings/fx", "/settings/fx/1", "/settings/fx/1/delete", "/settings/stages",
        "/settings/picklists/industry", "/settings/visibility", "/settings/teams", "/settings/teams/1", "/settings/teams/1/deactivate", "/settings/teams/1/reactivate",
    ];

    [SqlTheory]
    [InlineData("00000000-0000-4000-8000-00000000a002")]
    [InlineData("00000000-0000-4000-8000-00000000a003")]
    public async Task Manager_and_User_get_403_on_every_settings_verb(string oid)
    {
        var (f, c) = await AsAsync(oid);
        using var guard = f;
        foreach (var path in GetPaths)
        {
            await HttpHelpers.ExpectAsync(HttpStatusCode.Forbidden, await c.GetAsync(path));
        }
        var token = await HttpHelpers.TokenAsync(c, "/accounts/new");
        foreach (var path in PostPaths)
        {
            await HttpHelpers.ExpectAsync(HttpStatusCode.Forbidden, await c.PostAsync(path, HttpHelpers.Form(("_csrf", token), ("action", "add"), ("label", "x"), ("n", "0"))));
        }
    }

    [SqlFact]
    public async Task Admin_opens_every_page_and_the_nav_links_to_the_four_new_ones()
    {
        var (f, c) = await AsAsync(Admin);
        using var guard = f;
        foreach (var path in new[] { "/settings/locale", "/settings/fx", "/settings/stages", "/settings/picklists/industry", "/settings/picklists/lead_source", "/settings/picklists/lost_reason", "/settings/picklists/activity_type" })
        {
            await HttpHelpers.ExpectAsync(HttpStatusCode.OK, await c.GetAsync(path));
        }
        var doc = await HttpHelpers.ParseAsync(await c.GetAsync("/settings/stages"));
        Assert.Equal(["General", "Currency & locale", "FX rates", "Pipeline stages", "Picklists", "Visibility", "Users & teams"], doc.QuerySelectorAll("nav.subnav a").Select(a => a.TextContent).ToArray());
        Assert.Equal("page", doc.QuerySelector("nav.subnav a[href='/settings/stages']")!.GetAttribute("aria-current"));
        var pick = await c.GetAsync("/settings/picklists");
        Assert.Equal("/settings/picklists/industry", pick.Headers.Location!.OriginalString);
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await c.GetAsync("/settings/picklists/bogus"));
    }

    [SqlFact]
    public async Task Id_routes_refuse_non_canonical_ids_and_post_only_routes_have_no_get()
    {
        var (f, c) = await AsAsync(Admin);
        using var guard = f;
        foreach (var path in new[] { "/settings/fx/+5/edit", "/settings/fx/005/edit", "/settings/fx/%205/edit", "/settings/fx/2147483648/edit", "/settings/fx/0/edit", "/settings/teams/005/edit", "/accounts/+5", "/accounts/005", "/accounts/2147483648", "/accounts/%205/edit",
            "/settings/teams", "/settings/teams/1", "/settings/general/unknown", "/settings/fx/1", "/settings/fx/9999/edit" })
        {
            await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await c.GetAsync(path));
        }
        var token = await HttpHelpers.TokenAsync(c, "/settings/general");
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await c.PostAsync("/settings/general/unknown", HttpHelpers.Form(("_csrf", token))));
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await c.PostAsync("/settings/fx/1/edit", HttpHelpers.Form(("_csrf", token))));
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await c.PostAsync("/accounts/+5/delete", HttpHelpers.Form(("_csrf", token))));
    }

    // ---------------- currency and locale ----------------

    private static string Selected(AngleSharp.Dom.IDocument d, string sel) => d.QuerySelector($"{sel} option[selected]")!.TextContent;

    [SqlFact]
    public async Task Locale_validates_with_the_exact_copy_saves_with_audit_and_always_enables_the_base_currency()
    {
        var (f, c) = await AsAsync(Admin);
        using var guard = f;
        var before = await HttpHelpers.ParseAsync(await c.GetAsync("/settings/locale"));
        Assert.Equal("DKK", Selected(before, "#f-base"));
        Assert.Equal("da-DK", Selected(before, "#f-locale"));
        Assert.Equal("Europe/Copenhagen", Selected(before, "#f-timezone"));
        Assert.Contains("Preview (saved settings):", before.QuerySelector("main")!.TextContent, StringComparison.Ordinal);

        var bad = await PostAsync(c, "/settings/locale", "/settings/locale", ("base", "XXX"), ("locale", "fr-FR"), ("timezone", "Mars/Base"), ("firstDay", "9"), ("fiscalStart", "0"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, bad);
        var text = await TextAsync(bad);
        foreach (var msg in new[] { "Choose a base currency.", "Choose a locale.", "Choose a timezone.", "Choose the first day of the week.", "Choose a month." })
        {
            Assert.Contains(msg, text, StringComparison.Ordinal);
        }
        Assert.Contains("Fix 5 problems to save", text, StringComparison.Ordinal);

        try
        {
            var ok = await PostAsync(c, "/settings/locale", "/settings/locale", ("base", "DKK"), ("enabled", "USD"), ("enabled", "ZZZ"), ("locale", "en-US"), ("timezone", "UTC"), ("firstDay", "7"), ("fiscalStart", "4"));
            await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, ok);
            var after = await HttpHelpers.ParseAsync(await c.GetAsync("/settings/locale"));
            Assert.Equal("Settings saved.", after.QuerySelector(".flash")!.TextContent);
            Assert.Equal("en-US", Selected(after, "#f-locale"));
            Assert.Equal("UTC", Selected(after, "#f-timezone"));
            Assert.Equal("Sunday", Selected(after, "#f-firstDay"));
            Assert.Equal("April", Selected(after, "#f-fiscalStart"));
            var checkedBoxes = after.QuerySelectorAll("input[name=enabled]").Where(i => i.HasAttribute("checked")).Select(i => i.GetAttribute("value")).OrderBy(v => v, StringComparer.Ordinal).ToArray();
            Assert.Equal(["DKK", "USD"], checkedBoxes); // base always enabled, unknown codes dropped
            // formatting follows the new locale immediately: rate example for en-US
            var fx = await TextAsync(await c.GetAsync("/settings/fx"));
            Assert.Contains("Base currency: <strong>DKK</strong>", fx, StringComparison.Ordinal);
        }
        finally
        {
            await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, "/settings/locale", "/settings/locale", ("base", "DKK"), ("enabled", "DKK"), ("enabled", "EUR"), ("enabled", "USD"), ("enabled", "SEK"), ("enabled", "NOK"),
                ("locale", "da-DK"), ("timezone", "Europe/Copenhagen"), ("firstDay", "1"), ("fiscalStart", "1")));
        }
    }

    // ---------------- FX rates ----------------

    [SqlFact]
    public async Task Fx_rates_add_validate_duplicate_edit_confirm_audit_and_delete()
    {
        var (f, c) = await AsAsync(Admin);
        using var guard = f;
        var page = await c.GetAsync("/settings/fx");
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, page);
        Assert.Contains("Missing rate for", await TextAsync(page), StringComparison.Ordinal);

        var bad = await PostAsync(c, "/settings/fx", "/settings/fx", ("currency", ""), ("month", "2001-13"), ("rate", "abc"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, bad);
        var badText = await TextAsync(bad);
        Assert.Contains("Choose a currency.", badText, StringComparison.Ordinal);
        Assert.Contains("Choose a month.", badText, StringComparison.Ordinal);
        Assert.Contains("Enter a rate, for example 7,4590.", badText, StringComparison.Ordinal);
        // strict locale parsing (da-DK): '.' is a group separator, not allowed in rates; 7 decimals; negative; zero
        foreach (var rate in new[] { "7.459", "7,4590001", "7,459,1", "1e3", "" })
        {
            var r = await PostAsync(c, "/settings/fx", "/settings/fx", ("currency", "EUR"), ("month", "2001-03"), ("rate", rate));
            await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, r);
            Assert.Contains("Enter a rate, for example 7,4590.", await TextAsync(r), StringComparison.Ordinal);
        }
        foreach (var rate in new[] { "-1", "0", "0,000000" })
        {
            var r = await PostAsync(c, "/settings/fx", "/settings/fx", ("currency", "EUR"), ("month", "2001-03"), ("rate", rate));
            await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, r);
            Assert.Contains("Rate must be more than 0.", await TextAsync(r), StringComparison.Ordinal);
        }
        var notBase = await PostAsync(c, "/settings/fx", "/settings/fx", ("currency", "DKK"), ("month", "2001-03"), ("rate", "1"));
        Assert.Contains("Choose a currency.", await TextAsync(notBase), StringComparison.Ordinal);

        var add = await PostAsync(c, "/settings/fx", "/settings/fx", ("currency", "EUR"), ("month", "2001-03"), ("rate", "7,459123"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, add);
        var list = await HttpHelpers.ParseAsync(await c.GetAsync("/settings/fx?currency=EUR&year=2001"));
        Assert.Equal("Rate saved.", list.QuerySelector(".flash")!.TextContent);
        var row = list.QuerySelectorAll("table.data tbody tr").Single(r => r.TextContent.Contains("03.2001", StringComparison.Ordinal));
        Assert.Contains("7,459123", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Test Admin", row.TextContent, StringComparison.Ordinal);
        var id = int.Parse(row.QuerySelector("a[href$='/edit']")!.GetAttribute("href")!.Split('/')[3], CultureInfo.InvariantCulture);
        Assert.Equal("Edit EUR 03.2001", row.QuerySelector("a[href$='/edit']")!.TextContent);

        var dup = await PostAsync(c, "/settings/fx", "/settings/fx", ("currency", "EUR"), ("month", "2001-03"), ("rate", "8"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, dup);
        var dupDoc = await HttpHelpers.ParseAsync(dup);
        Assert.Contains("A rate for EUR in 03.2001 already exists.", dupDoc.QuerySelector("#e-currency")!.TextContent, StringComparison.Ordinal);
        Assert.Equal($"/settings/fx/{id}/edit", dupDoc.QuerySelector("a[href$='/edit']:not(table a)")!.GetAttribute("href"));

        var edit = await HttpHelpers.ParseAsync(await c.GetAsync($"/settings/fx/{id}/edit"));
        Assert.Equal("7,459123", edit.QuerySelector("#f-rate")!.GetAttribute("value"));
        var unchanged = await PostAsync(c, $"/settings/fx/{id}/edit", $"/settings/fx/{id}", ("currency", "EUR"), ("month", "2001-03"), ("rate", "7,459123"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, unchanged); // nothing changes: no confirmation
        var ask = await PostAsync(c, $"/settings/fx/{id}/edit", $"/settings/fx/{id}", ("currency", "EUR"), ("month", "2001-03"), ("rate", "7,5"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, ask);
        Assert.Contains("This changes totals on the dashboard and in analytics for 03.2001. Continue?", await TextAsync(ask), StringComparison.Ordinal);
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, $"/settings/fx/{id}/edit", $"/settings/fx/{id}", ("currency", "EUR"), ("month", "2001-03"), ("rate", "7,5"), ("confirm", "1")));

        var del = await c.GetAsync($"/settings/fx/{id}/delete");
        Assert.Contains("Delete the EUR rate for 03.2001?", await TextAsync(del), StringComparison.Ordinal);
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, $"/settings/fx/{id}/delete", $"/settings/fx/{id}/delete"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await PostAsync(c, "/settings/fx", $"/settings/fx/{id}/delete"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await c.GetAsync($"/settings/fx/{id}/edit"));

        var audit = await AuditAsync("fxrate", id);
        Assert.Equal([("C", null, null, "EUR 2001-03 7.459123"), ("U", "Rate", "7.459123", "7.5"), ("D", null, "EUR 2001-03 7.5", null)], audit.Select(a => (a.Action, a.Field, a.Old, a.New)).ToArray());
    }

    [SqlFact]
    public async Task Rates_of_an_earlier_base_currency_are_not_listed_and_not_found_for_edit_or_delete()
    {
        var (f, c) = await AsAsync(Admin);
        using var guard = f;
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, "/settings/fx", "/settings/fx", ("currency", "USD"), ("month", "2002-05"), ("rate", "6,5")));
        var row = (await HttpHelpers.ParseAsync(await c.GetAsync("/settings/fx?currency=USD&year=2002"))).QuerySelectorAll("table.data tbody tr").Single(r => r.TextContent.Contains("05.2002", StringComparison.Ordinal));
        var id = int.Parse(row.QuerySelector("a[href$='/edit']")!.GetAttribute("href")!.Split('/')[3], CultureInfo.InvariantCulture);
        try
        {
            await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, "/settings/locale", "/settings/locale", ("base", "EUR"), ("enabled", "DKK"), ("enabled", "USD"), ("locale", "da-DK"), ("timezone", "Europe/Copenhagen"), ("firstDay", "1"), ("fiscalStart", "1")));
            Assert.DoesNotContain("05.2002", await TextAsync(await c.GetAsync("/settings/fx?year=2002")), StringComparison.Ordinal);
            await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await c.GetAsync($"/settings/fx/{id}/edit"));
            await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await c.GetAsync($"/settings/fx/{id}/delete"));
            await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await PostAsync(c, "/settings/fx", $"/settings/fx/{id}/delete"));
            await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await PostAsync(c, "/settings/fx", $"/settings/fx/{id}", ("currency", "USD"), ("month", "2002-05"), ("rate", "9"), ("confirm", "1")));
        }
        finally
        {
            await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, "/settings/locale", "/settings/locale", ("base", "DKK"), ("enabled", "DKK"), ("enabled", "EUR"), ("enabled", "USD"), ("enabled", "SEK"), ("enabled", "NOK"),
                ("locale", "da-DK"), ("timezone", "Europe/Copenhagen"), ("firstDay", "1"), ("fiscalStart", "1")));
        }
        // back on the original base the rate is visible and untouched, then removed
        Assert.Contains("6,5", await TextAsync(await c.GetAsync("/settings/fx?year=2002")), StringComparison.Ordinal);
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, "/settings/fx", $"/settings/fx/{id}/delete"));
    }

    // ---------------- pipeline stages ----------------

    private sealed record StageRow(string Id, string Name, string Prob, string Type);

    private string _ver = "";

    /// <summary>Reads the editor rows and remembers the page's stage-list version, which every later POST sends back.</summary>
    private List<StageRow> ReadStages(IHtmlDocument d)
    {
        _ver = d.QuerySelector("input[name=ver]")!.GetAttribute("value")!;
        var n = int.Parse(d.QuerySelector("input[name=n]")!.GetAttribute("value")!, CultureInfo.InvariantCulture);
        return Enumerable.Range(0, n).Select(i => new StageRow(
            d.QuerySelector($"input[name=id_{i}]")!.GetAttribute("value")!, d.QuerySelector($"input[name=name_{i}]")!.GetAttribute("value")!,
            d.QuerySelector($"input[name=prob_{i}]")!.GetAttribute("value")!, d.QuerySelector($"input[name=type_{i}][checked]")!.GetAttribute("value")!)).ToList();
    }

    private (string, string)[] StageFields(IReadOnlyList<StageRow> rows, string action) =>
        [("n", rows.Count.ToString(CultureInfo.InvariantCulture)), ("ver", _ver), ("action", action),
            .. rows.SelectMany((r, i) => new[] { ($"id_{i}", r.Id), ($"name_{i}", r.Name), ($"prob_{i}", r.Prob), ($"type_{i}", r.Type) })];

    [SqlFact]
    public async Task Stages_add_move_and_remove_only_rerender_and_save_validates_audits_and_refuses_in_use_removal()
    {
        var (f, c) = await AsAsync(Admin);
        using var guard = f;
        var doc = await HttpHelpers.ParseAsync(await c.GetAsync("/settings/stages"));
        var rows = ReadStages(doc);
        Assert.Equal(["Qualification", "Needs analysis", "Proposal", "Negotiation", "Closed won", "Closed lost"], rows.Take(6).Select(r => r.Name).ToArray());
        Assert.Equal("disabled", doc.QuerySelector("#up-0")!.GetAttribute("disabled"));

        var added = await HttpHelpers.ParseAsync(await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(rows, "add")));
        Assert.Equal(rows.Count + 1, ReadStages(added).Count);
        Assert.Contains("Changes are not saved yet. Choose Save stages to keep them.", added.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(added.QuerySelector($"#f-name{rows.Count}[data-autofocus]"));
        Assert.Equal(rows.Count, ReadStages(await HttpHelpers.ParseAsync(await c.GetAsync("/settings/stages"))).Count); // nothing saved yet

        var moved = await HttpHelpers.ParseAsync(await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(rows, "down:1")));
        var movedRows = ReadStages(moved);
        Assert.Equal("Proposal", movedRows[1].Name);
        Assert.Equal("Needs analysis", movedRows[2].Name);
        Assert.NotNull(moved.QuerySelector("#down-2[data-autofocus]"));

        // validation: blank name, duplicate name, probability, two Won stages
        var broken = rows.Select(r => r).ToList();
        broken[0] = broken[0] with { Name = "" };
        broken[1] = broken[1] with { Name = "PROPOSAL" };
        broken[3] = broken[3] with { Prob = "101" };
        broken[5] = broken[5] with { Type = "won" };
        var inv = await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(broken, "save"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, inv);
        var invText = await HttpHelpers.ParseAsync(inv);
        var summary = invText.QuerySelector("#error-summary")!.TextContent;
        Assert.Contains("Row 1: Enter a stage name.", summary, StringComparison.Ordinal);
        Assert.Contains("Row 4: Probability must be between 0 and 100.", summary, StringComparison.Ordinal);
        Assert.Contains("You need exactly one Won stage and one Lost stage.", summary, StringComparison.Ordinal);
        Assert.Equal("#f-name0", invText.QuerySelector("#error-summary a")!.GetAttribute("href"));

        // save a new stage, rename it, then remove it again
        var name = Unique("Stage");
        var withNew = rows.Append(new StageRow("", name, "30", "open")).ToList();
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(withNew, "save")));
        var saved = await HttpHelpers.ParseAsync(await c.GetAsync("/settings/stages"));
        Assert.Equal("Settings saved.", saved.QuerySelector(".flash")!.TextContent);
        var savedRows = ReadStages(saved);
        var newRow = savedRows.Single(r => r.Name == name);
        Assert.Equal("30", newRow.Prob);
        var newIdx = savedRows.IndexOf(newRow);
        var renamed = savedRows.ToList();
        renamed[newIdx] = newRow with { Name = name + "x", Prob = "35" };
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(renamed, "save")));
        var stageId = int.Parse(newRow.Id, CultureInfo.InvariantCulture);
        ReadStages(await HttpHelpers.ParseAsync(await c.GetAsync("/settings/stages"))); // the save changed the list: pick up the new version
        var removeDoc = await HttpHelpers.ParseAsync(await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(renamed, $"remove:{newIdx}")));
        Assert.DoesNotContain(ReadStages(removeDoc), r => r.Name == name + "x");
        var afterRemove = renamed.Where((_, i) => i != newIdx).ToList();
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(afterRemove, "save")));
        Assert.DoesNotContain(ReadStages(await HttpHelpers.ParseAsync(await c.GetAsync("/settings/stages"))), r => r.Name == name + "x");
        Assert.Equal([("C", null), ("U", "Name"), ("U", "Probability"), ("D", null)], (await AuditAsync("stage", stageId)).Select(a => (a.Action, a.Field)).ToArray());

        // L4: a name starting with "~" is refused (it is the placeholder used while saving)
        var tilde = rows.Select(r => r).ToList();
        tilde[2] = tilde[2] with { Name = "~sneaky" };
        var tildeRes = await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(tilde, "save"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, tildeRes);
        Assert.Contains("can&#x27;t start with ~.", await TextAsync(tildeRes), StringComparison.Ordinal);

        // L3: a save made from an older copy of the page is refused, and nothing changes
        var staleVer = _ver;
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(rows, "save")));
        var current = ReadStages(await HttpHelpers.ParseAsync(await c.GetAsync("/settings/stages")));
        Assert.NotEqual(staleVer, _ver);
        var versionNow = _ver;
        _ver = staleVer;
        var staleRes = await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(current.Select((r, i) => i == 0 ? r with { Name = "Stale edit" } : r).ToList(), "save"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, staleRes);
        Assert.Contains("The stage list was changed by someone else since you opened this page.", await TextAsync(staleRes), StringComparison.Ordinal);
        _ver = versionNow;
        var noVer = await c.PostAsync("/settings/stages", HttpHelpers.Form([("_csrf", await HttpHelpers.TokenAsync(c, "/settings/stages")), .. StageFields(current, "save").Where(f => f.Item1 != "ver")]));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, noVer);
        Assert.Equal("Needs analysis", ReadStages(await HttpHelpers.ParseAsync(await c.GetAsync("/settings/stages")))[1].Name);

        // the only Won stage cannot be removed
        var wonIdx = rows.FindIndex(r => r.Type == "won");
        var wonRefused = await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(rows, $"remove:{wonIdx}"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, wonRefused);
        Assert.Contains("Can't remove the only Won stage. Make another stage Won first.", WebUtility.HtmlDecode(await TextAsync(wonRefused)), StringComparison.Ordinal);

        // a stage in use cannot be removed (immediately refused, the row stays); a stale/forged id list changes nothing
        var (f2, u) = await AsAsync(UserA1);
        using var guard2 = f2;
        var me = await UserIdAsync(UserA1);
        var acc = await PostAsync(u, "/accounts/new", "/accounts", ("Name", Unique("StageAcc")), ("OwnerId", me.ToString(CultureInfo.InvariantCulture)));
        var accId = acc.Headers.Location!.OriginalString.Split('/')[2];
        var stage1 = rows[0].Id;
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(u, "/opportunities/new", "/opportunities", ("Name", "InUse"), ("AccountId", accId), ("StageId", stage1), ("Amount", "10"), ("CurrencyCode", "DKK"), ("CloseDate", "2099-01-01"), ("OwnerId", me.ToString(CultureInfo.InvariantCulture))));
        var inUse = await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(rows, "remove:0"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, inUse);
        var inUseText = await TextAsync(inUse);
        Assert.Contains("Can't remove \"Qualification\":", WebUtility.HtmlDecode(inUseText), StringComparison.Ordinal);
        Assert.Contains("View these opportunities", inUseText, StringComparison.Ordinal);
        // L5: the type of a stage in use cannot change (swap Open/Lost: Qualification becomes Lost, Closed lost becomes Open)
        var swapped = rows.Select(r => r).ToList();
        swapped[0] = swapped[0] with { Type = "lost" };
        swapped[5] = swapped[5] with { Type = "open", Prob = "5" };
        var typeRes = await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(swapped, "save"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, typeRes);
        Assert.Contains("Can't change the type of \"Qualification\":", WebUtility.HtmlDecode(await TextAsync(typeRes)), StringComparison.Ordinal);
        var forged = rows.Append(new StageRow("2147483647", "Ghost", "10", "open")).ToList();
        var forgedRes = await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(forged, "save"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, forgedRes);
        Assert.Contains("The stage list is out of date. Reload the page and try again.", await TextAsync(forgedRes), StringComparison.Ordinal);
        var dupIds = rows.Append(rows[0] with { Name = "Other name" }).ToList();
        Assert.Contains("The stage list is out of date.", await TextAsync(await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(dupIds, "save"))), StringComparison.Ordinal);
        Assert.Equal(rows, ReadStages(await HttpHelpers.ParseAsync(await c.GetAsync("/settings/stages"))));
        // removing the in-use stage through a forged Save is refused by the repository as well
        var withoutFirst = rows.Skip(1).ToList();
        var refused = await PostAsync(c, "/settings/stages", "/settings/stages", StageFields(withoutFirst, "save"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, refused);
        Assert.Contains("before deleting it.", await TextAsync(refused), StringComparison.Ordinal);
        Assert.Equal(rows, ReadStages(await HttpHelpers.ParseAsync(await c.GetAsync("/settings/stages"))));
    }

    private async Task<int> UserIdAsync(string oid)
    {
        await using var c = new SqlConnection(SqlConnectionFactory.Build(fx.OwnerDb, "owner-cfg", 1, false, 30));
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT UserId FROM crm.Users WHERE EntraOid = @o", c);
        cmd.Parameters.AddWithValue("@o", Guid.Parse(oid));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    // ---------------- picklists ----------------

    private static (string Id, string Label)[] ValuesOf(IHtmlDocument d) =>
        d.QuerySelectorAll("form input[name=label][id^=f-label]").Where(i => i.Id != "f-label")
            .Select(i => (i.ParentElement!.QuerySelector("input[name=valueId]")!.GetAttribute("value")!, i.GetAttribute("value")!)).ToArray();

    [SqlFact]
    public async Task Picklists_add_rename_deactivate_reactivate_and_reorder_with_audit_and_no_duplicates()
    {
        var (f, c) = await AsAsync(Admin);
        using var guard = f;
        const string path = "/settings/picklists/lead_source";
        var label = Unique("Src");
        var empty = await PostAsync(c, path, path, ("action", "add"), ("label", "  "));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, empty);
        Assert.Contains("Enter a name.", await TextAsync(empty), StringComparison.Ordinal);
        var tooLong = await PostAsync(c, path, path, ("action", "add"), ("label", new string('x', 81)));
        Assert.Contains("Name must be 80 characters or fewer.", await TextAsync(tooLong), StringComparison.Ordinal);
        var dupSeed = await PostAsync(c, path, path, ("action", "add"), ("label", "Website"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, dupSeed);
        Assert.Contains("\"Website\" already exists in this list.", WebUtility.HtmlDecode(await TextAsync(dupSeed)), StringComparison.Ordinal);

        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, path, path, ("action", "add"), ("label", label)));
        var doc = await HttpHelpers.ParseAsync(await c.GetAsync(path));
        Assert.Equal("Settings saved.", doc.QuerySelector(".flash")!.TextContent);
        var values = ValuesOf(doc);
        var (id, _) = values.Single(v => v.Label == label);
        Assert.Equal(label, values[^1].Label); // appended last
        var idInt = int.Parse(id, CultureInfo.InvariantCulture);

        var renamed = label + "-r";
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, path, path, ("action", "rename"), ("valueId", id), ("label", renamed)));
        var dupRename = await PostAsync(c, path, path, ("action", "rename"), ("valueId", id), ("label", "Website"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, dupRename);
        Assert.Contains("already exists in this list.", (await HttpHelpers.ParseAsync(dupRename)).QuerySelector($"#e-label{id}")!.TextContent, StringComparison.Ordinal);

        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, path, path, ("action", "deactivate"), ("valueId", id)));
        var inactive = await HttpHelpers.ParseAsync(await c.GetAsync(path));
        Assert.Contains("Reactivate " + renamed, inactive.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, path, path, ("action", "activate"), ("valueId", id)));

        var up = await PostAsync(c, path, path, ("action", "up"), ("valueId", id));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, up);
        Assert.Equal($"{path}?focus={id}-up", up.Headers.Location!.OriginalString);
        var after = await HttpHelpers.ParseAsync(await c.GetAsync(up.Headers.Location.OriginalString));
        var order = ValuesOf(after).Select(v => v.Label).ToList();
        Assert.Equal(renamed, order[^2]);
        Assert.NotNull(after.QuerySelector($"#up-{id}[data-autofocus]"));
        // moving the last value down is a no-op; the first value cannot move up
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, path, path, ("action", "down"), ("valueId", id)));
        Assert.Equal(renamed, ValuesOf(await HttpHelpers.ParseAsync(await c.GetAsync(path))).Last().Label);

        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await PostAsync(c, path, path, ("action", "rename"), ("valueId", "abc"), ("label", "x")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, await PostAsync(c, path, "/settings/picklists/nope", ("action", "add")));
        var missing = await PostAsync(c, path, path, ("action", "rename"), ("valueId", "2147483647"), ("label", "x"));
        Assert.Contains("Value not found.", await TextAsync(missing), StringComparison.Ordinal);
        var unknown = await PostAsync(c, path, path, ("action", "explode"), ("valueId", id));
        Assert.Contains("Unknown action.", await TextAsync(unknown), StringComparison.Ordinal);

        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, path, path, ("action", "deactivate"), ("valueId", id)));
        var audit = await AuditAsync("picklistvalue", idInt);
        Assert.Equal(["C", "ULabel", "UIsActive", "UIsActive", "USortOrder", "USortOrder", "UIsActive"], audit.Select(a => a.Action + a.Field).ToArray());
        Assert.Equal(("IsActive", "1", "0"), (audit[2].Field!, audit[2].Old!, audit[2].New!));
    }

    [SqlFact]
    public async Task Activity_types_cannot_be_added_but_can_be_renamed()
    {
        var (f, c) = await AsAsync(Admin);
        using var guard = f;
        const string path = "/settings/picklists/activity_type";
        var doc = await HttpHelpers.ParseAsync(await c.GetAsync(path));
        Assert.Null(doc.QuerySelector("#f-label"));
        Assert.Contains("The four activity types are fixed", doc.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
        var add = await PostAsync(c, path, path, ("action", "add"), ("label", "Visit"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, add);
        Assert.Contains("Values cannot be added to this list.", await TextAsync(add), StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task New_pages_have_no_inline_script_or_style_and_escape_values()
    {
        var (f, c) = await AsAsync(Admin);
        using var guard = f;
        foreach (var path in new[] { "/settings/locale", "/settings/fx", "/settings/stages", "/settings/picklists/industry" })
        {
            var doc = await HttpHelpers.ParseAsync(await c.GetAsync(path));
            Assert.Empty(doc.QuerySelectorAll("script:not([src])"));
            Assert.Empty(doc.QuerySelectorAll("[style]"));
        }
        const string evil = "<script>alert(1)</script>";
        var r = await PostAsync(c, "/settings/picklists/industry", "/settings/picklists/industry", ("action", "add"), ("label", evil));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, r);
        var html = await TextAsync(await c.GetAsync("/settings/picklists/industry"));
        Assert.DoesNotContain(evil, html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        var (id, _) = ValuesOf(await HttpHelpers.ParseAsync(new HttpResponseMessage { Content = new StringContent(html) })).Single(v => v.Label == evil);
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await PostAsync(c, "/settings/picklists/industry", "/settings/picklists/industry", ("action", "deactivate"), ("valueId", id)));
    }
}
