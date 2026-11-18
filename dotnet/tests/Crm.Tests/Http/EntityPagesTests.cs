using System.Net;
using System.Text.RegularExpressions;
using Crm.Web.Data;
using Microsoft.Data.SqlClient;

namespace Crm.Tests.Http;

/// <summary>Ports of the highest-value crmHttp/qaIncrement1-2 behaviours, over real SQL with the real sign-in flow and RLS.</summary>
[Collection("sql")]
public sealed partial class EntityPagesTests(SqlServerFixture fx)
{
    private const string Admin = "00000000-0000-4000-8000-00000000a001";
    private const string ManagerA = "00000000-0000-4000-8000-00000000a002";
    private const string UserA1 = "00000000-0000-4000-8000-00000000a003";
    private const string UserA2 = "00000000-0000-4000-8000-00000000a004";
    private const string ManagerB = "00000000-0000-4000-8000-00000000b001";

    [GeneratedRegex(@"^/accounts/([0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccountPath();

    private async Task ExecOwnerAsync(string sql)
    {
        await using var c = new SqlConnection(SqlConnectionFactory.Build(fx.OwnerDb, "owner-ent", 1, false, 30));
        await c.OpenAsync();
        await SqlServerFixture.ExecAsync(c, sql);
    }

    private async Task<int> UserIdAsync(string oid)
    {
        await using var c = new SqlConnection(SqlConnectionFactory.Build(fx.OwnerDb, "owner-ent", 1, false, 30));
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT UserId FROM crm.Users WHERE EntraOid = @o", c);
        cmd.Parameters.AddWithValue("@o", Guid.Parse(oid));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(CrmFactory F, HttpClient C)> AsAsync(string oid)
    {
        var f = new CrmFactory(fx.AppDb);
        var c = HttpHelpers.NewClient(f);
        var r = await HttpHelpers.SignInAsync(c, oid);
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, r);
        return (f, c);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string formPath, string postPath, params (string, string)[] fields)
    {
        var token = await HttpHelpers.TokenAsync(c, formPath);
        return await c.PostAsync(postPath, HttpHelpers.Form([("_csrf", token), .. fields]));
    }

    private static async Task<int> CreateAccountAsync(HttpClient c, string name, int ownerId)
    {
        var r = await PostAsync(c, "/accounts/new", "/accounts", ("Name", name), ("OwnerId", ownerId.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("City", "Aarhus"));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, r);
        var m = AccountPath().Match(r.Headers.Location!.OriginalString);
        Assert.True(m.Success, r.Headers.Location.OriginalString);
        return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Unique(string p) => p + "-" + Guid.NewGuid().ToString("N")[..8];

    [SqlFact]
    public async Task Create_redirects_with_flash_audits_and_shows_history()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var name = Unique("Acme");
        var id = await CreateAccountAsync(c, name, await UserIdAsync(UserA1));
        var page = await c.GetAsync($"/accounts/{id}");
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, page);
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
        var doc = await HttpHelpers.ParseAsync(page);
        Assert.Equal("Account saved.", doc.QuerySelector(".flash")!.TextContent);
        Assert.Equal(name, doc.QuerySelector("h1")!.TextContent);
        Assert.Contains("Created", doc.QuerySelector("ol.timeline")!.TextContent, StringComparison.Ordinal);
        var list = await HttpHelpers.ParseAsync(await c.GetAsync($"/accounts?q={name}"));
        Assert.Contains(name, list.QuerySelector("table.data")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 account found", list.QuerySelector("p[role=status]")!.TextContent, StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task Validation_errors_return_422_keep_input_and_use_the_exact_copy()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var r = await PostAsync(c, "/accounts/new", "/accounts", ("Name", ""), ("Website", "nope"), ("City", "Keepville"), ("OwnerId", (await UserIdAsync(UserA1)).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, r);
        var doc = await HttpHelpers.ParseAsync(r);
        Assert.True("Fix 2 problems to save" == doc.QuerySelector("#error-summary h2")!.TextContent, doc.QuerySelector("#error-summary")!.TextContent);
        Assert.Contains("Enter an account name.", doc.QuerySelector("#e-Name")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("Keepville", doc.QuerySelector("#f-City")!.GetAttribute("value"));
        Assert.Equal("true", doc.QuerySelector("#f-Name")!.GetAttribute("aria-invalid"));
    }

    [SqlFact]
    public async Task Posts_need_an_antiforgery_token()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var r = await c.PostAsync("/accounts", HttpHelpers.Form(("Name", "x"), ("OwnerId", "1")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.Forbidden, r);
    }

    [SqlFact]
    public async Task Private_mode_hides_other_users_records_with_404_and_admin_sees_all()
    {
        var (f1, a1) = await AsAsync(UserA1);
        using var _1 = f1;
        var name = Unique("Hidden");
        var id = await CreateAccountAsync(a1, name, await UserIdAsync(UserA1));
        var (f2, a2) = await AsAsync(UserA2);
        using var _2 = f2;
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, (await a2.GetAsync($"/accounts/{id}")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, (await a2.GetAsync($"/accounts/{id}/edit")));
        var hiddenList = await HttpHelpers.ParseAsync(await a2.GetAsync($"/accounts?q={name}"));
        Assert.Null(hiddenList.QuerySelector("table.data"));
        Assert.Contains("0 accounts found", hiddenList.QuerySelector("p[role=status]")!.TextContent, StringComparison.Ordinal);
        var (f3, adm) = await AsAsync(Admin);
        using var _3 = f3;
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, (await adm.GetAsync($"/accounts/{id}")));
        var (f4, mgrB) = await AsAsync(ManagerB);
        using var _4 = f4;
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, (await mgrB.GetAsync($"/accounts/{id}")));
    }

    [SqlFact]
    public async Task Edit_saves_and_a_stale_RowVer_gives_the_409_conflict_page_without_overwriting()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var me = await UserIdAsync(UserA1);
        var id = await CreateAccountAsync(c, Unique("Edit"), me);
        var editDoc = await HttpHelpers.ParseAsync(await c.GetAsync($"/accounts/{id}/edit"));
        var rowVer = editDoc.QuerySelector("input[name=rowVer]")!.GetAttribute("value")!;
        var token = editDoc.QuerySelector("input[name=_csrf]")!.GetAttribute("value")!;
        var ok = await c.PostAsync($"/accounts/{id}", HttpHelpers.Form(("_csrf", token), ("rowVer", rowVer), ("Name", "Renamed"), ("OwnerId", me.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, ok);
        var stale = await c.PostAsync($"/accounts/{id}", HttpHelpers.Form(("_csrf", token), ("rowVer", rowVer), ("Name", "Stale overwrite"), ("OwnerId", me.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        await HttpHelpers.ExpectAsync(HttpStatusCode.Conflict, stale);
        var doc = await HttpHelpers.ParseAsync(stale);
        Assert.Equal("Someone else changed this record", doc.QuerySelector("h1")!.TextContent);
        var detail = await (await c.GetAsync($"/accounts/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("Renamed", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale overwrite", detail, StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task A_user_cannot_pick_an_owner_outside_the_team_and_the_form_lists_only_team_mates()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var other = await UserIdAsync(ManagerB);
        var r = await PostAsync(c, "/accounts/new", "/accounts", ("Name", Unique("X")), ("OwnerId", other.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, r);
        Assert.Contains("Choose an owner from your team.", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var form = await HttpHelpers.ParseAsync(await c.GetAsync("/accounts/new"));
        var labels = form.QuerySelectorAll("#f-OwnerId option").Select(o => o.TextContent).ToList();
        Assert.DoesNotContain(labels, l => l.Contains("Manager B", StringComparison.Ordinal));
        Assert.Contains(labels, l => l.Contains("Test Manager A", StringComparison.Ordinal));
    }

    [SqlFact]
    public async Task Hand_off_to_a_team_mate_who_makes_it_invisible_redirects_to_the_list_with_the_notice()
    {
        var (f, c) = await AsAsync(Admin);
        using var _ = f;
        // Admin creates a record owned by User A1 in private mode: Admin still sees everything, so it is visible.
        var id = await CreateAccountAsync(c, Unique("Admin-owned"), await UserIdAsync(UserA1));
        Assert.True(id > 0);
        var (f2, a1) = await AsAsync(UserA1);
        using var _2 = f2;
        // A1 hands a record to A2 (team-mate): in private mode A1 can no longer see it.
        var mine = await CreateAccountAsync(a1, Unique("Handoff"), await UserIdAsync(UserA1));
        var edit = await HttpHelpers.ParseAsync(await a1.GetAsync($"/accounts/{mine}/edit"));
        var r = await a1.PostAsync($"/accounts/{mine}", HttpHelpers.Form(
            ("_csrf", edit.QuerySelector("input[name=_csrf]")!.GetAttribute("value")!), ("rowVer", edit.QuerySelector("input[name=rowVer]")!.GetAttribute("value")!),
            ("Name", "Handed"), ("OwnerId", (await UserIdAsync(UserA2)).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, r);
        Assert.Equal("/accounts", r.Headers.Location!.OriginalString);
        var list = await HttpHelpers.ParseAsync(await a1.GetAsync("/accounts"));
        Assert.Contains("Saved and assigned to Test User A2.", list.QuerySelector(".flash")!.TextContent, StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task Delete_asks_first_removes_the_record_and_hidden_records_are_404()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var id = await CreateAccountAsync(c, Unique("Doomed"), await UserIdAsync(UserA1));
        var confirm = await HttpHelpers.ParseAsync(await c.GetAsync($"/accounts/{id}/delete"));
        Assert.StartsWith("Delete ", confirm.QuerySelector("h1")!.TextContent, StringComparison.Ordinal);
        var token = confirm.QuerySelector("input[name=_csrf]")!.GetAttribute("value")!;
        var r = await c.PostAsync($"/accounts/{id}/delete", HttpHelpers.Form(("_csrf", token)));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, r);
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, (await c.GetAsync($"/accounts/{id}")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, (await c.PostAsync($"/accounts/{id}/delete", HttpHelpers.Form(("_csrf", token)))));
    }

    [SqlFact]
    public async Task Opportunity_amount_is_parsed_in_the_settings_locale_and_won_needs_a_confirmation_step()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var me = (await UserIdAsync(UserA1)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var acc = await CreateAccountAsync(c, Unique("OppAcc"), await UserIdAsync(UserA1));
        var form = await HttpHelpers.ParseAsync(await c.GetAsync("/opportunities/new"));
        var stage = form.QuerySelectorAll("#f-StageId option").First().GetAttribute("value")!;
        var bad = await PostAsync(c, "/opportunities/new", "/opportunities", ("Name", "Deal"), ("AccountId", acc.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("StageId", stage), ("Amount", "12.5"), ("CurrencyCode", "DKK"), ("CloseDate", "2099-12-31"), ("OwnerId", me));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, bad);
        Assert.Contains("Enter an amount, for example 12.500,00.", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var good = await PostAsync(c, "/opportunities/new", "/opportunities", ("Name", "Deal"), ("AccountId", acc.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("StageId", stage), ("Amount", "1.234,50"), ("CurrencyCode", "DKK"), ("CloseDate", "2099-12-31"), ("OwnerId", me));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, good);
        var detail = await HttpHelpers.ParseAsync(await c.GetAsync(good.Headers.Location!.OriginalString));
        Assert.Contains("DKK 1.234,50", detail.QuerySelector("dl.details")!.TextContent, StringComparison.Ordinal);
        var rowVer = detail.QuerySelector("input[name=rowVer]")!.GetAttribute("value")!;
        var token = detail.QuerySelector("input[name=_csrf]")!.GetAttribute("value")!;
        var oppPath = good.Headers.Location.OriginalString;
        var confirm = await c.PostAsync(oppPath + "/won", HttpHelpers.Form(("_csrf", token), ("rowVer", rowVer)));
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, confirm);
        var confirmDoc = await HttpHelpers.ParseAsync(confirm);
        Assert.Contains("The close date will be set to today.", confirmDoc.QuerySelector("main p")!.TextContent, StringComparison.Ordinal);
        var token2 = confirmDoc.QuerySelector("input[name=_csrf]")!.GetAttribute("value")!;
        var done = await c.PostAsync(oppPath + "/won", HttpHelpers.Form(("_csrf", token2), ("rowVer", rowVer), ("lostReason", ""), ("confirm", "1")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, done);
        var after = await HttpHelpers.ParseAsync(await c.GetAsync(oppPath));
        Assert.Contains("Opportunity marked as won. Close date set to today.", after.QuerySelector(".flash")!.TextContent, StringComparison.Ordinal);
        var stale = await c.PostAsync(oppPath + "/won", HttpHelpers.Form(("_csrf", token2), ("rowVer", rowVer), ("confirm", "1")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.Conflict, stale);
    }

    [SqlFact]
    public async Task Lost_requires_a_reason()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var me = (await UserIdAsync(UserA1)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var acc = await CreateAccountAsync(c, Unique("LostAcc"), await UserIdAsync(UserA1));
        var form = await HttpHelpers.ParseAsync(await c.GetAsync("/opportunities/new"));
        var stage = form.QuerySelectorAll("#f-StageId option").First().GetAttribute("value")!;
        var good = await PostAsync(c, "/opportunities/new", "/opportunities", ("Name", "L"), ("AccountId", acc.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("StageId", stage), ("Amount", "10"), ("CurrencyCode", "DKK"), ("CloseDate", "2099-01-01"), ("OwnerId", me));
        var detail = await HttpHelpers.ParseAsync(await c.GetAsync(good.Headers.Location!.OriginalString));
        var r = await c.PostAsync(good.Headers.Location.OriginalString + "/lost", HttpHelpers.Form(
            ("_csrf", detail.QuerySelector("input[name=_csrf]")!.GetAttribute("value")!), ("rowVer", detail.QuerySelector("input[name=rowVer]")!.GetAttribute("value")!), ("lostReason", "")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, r);
        Assert.Contains("Choose why this was lost.", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task Activities_need_a_link_notes_drop_due_dates_and_done_toggles_with_a_safe_return()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var me = (await UserIdAsync(UserA1)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var acc = (await CreateAccountAsync(c, Unique("ActAcc"), await UserIdAsync(UserA1))).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var none = await PostAsync(c, "/activities/new", "/activities", ("ActivityType", "call"), ("Subject", "Ring"), ("OwnerId", me));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, none);
        Assert.Contains("Link this activity to an account, contact or opportunity.", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var ok = await PostAsync(c, "/activities/new", "/activities", ("ActivityType", "note"), ("Subject", "Memo"), ("AccountId", acc), ("DueDate", "2030-01-01"), ("OwnerId", me));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, ok);
        var path = ok.Headers.Location!.OriginalString;
        var detail = await HttpHelpers.ParseAsync(await c.GetAsync(path));
        var token = detail.QuerySelector("input[name=_csrf]")!.GetAttribute("value")!;
        var done = await c.PostAsync(path + "/done", HttpHelpers.Form(("_csrf", token), ("returnTo", "https://evil.example/")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, done);
        Assert.Equal("/activities", done.Headers.Location!.OriginalString);
        var again = await HttpHelpers.ParseAsync(await c.GetAsync(path));
        Assert.Contains("Marked as done.", again.QuerySelector(".flash")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("Mark as not done", again.QuerySelector(".actions form button")!.TextContent);
        await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, (await c.PostAsync("/activities/2147483647/done", HttpHelpers.Form(("_csrf", token)))));
    }

    [SqlFact]
    public async Task Unknown_sort_keys_and_junk_filters_fall_back_instead_of_failing()
    {
        var (f, c) = await AsAsync(Admin);
        using var _ = f;
        foreach (var url in new[] { "/accounts?sort=Name;DROP TABLE x&dir=sideways", "/accounts?owner=abc&industry=1e5&page=-3&pageSize=7", "/opportunities?status=bogus&from=2026-02-30", "/activities?due=%27%20OR%201=1", "/contacts?q=%25_%5B" })
        {
            await HttpHelpers.ExpectAsync(HttpStatusCode.OK, (await c.GetAsync(url)));
        }
    }

    [SqlFact]
    public async Task Invalid_ids_and_unknown_entities_are_404()
    {
        var (f, c) = await AsAsync(Admin);
        using var _ = f;
        foreach (var url in new[] { "/accounts/0", "/accounts/abc", "/accounts/99999999999", "/widgets", "/accounts/1/unknown" })
        {
            await HttpHelpers.ExpectAsync(HttpStatusCode.NotFound, (await c.GetAsync(url)));
        }
    }

    [SqlFact]
    public async Task User_content_is_escaped_and_pages_have_no_inline_script()
    {
        var (f, c) = await AsAsync(UserA1);
        using var _ = f;
        var evil = "<script>alert(1)</script>";
        var r = await PostAsync(c, "/accounts/new", "/accounts", ("Name", evil), ("OwnerId", (await UserIdAsync(UserA1)).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var html = await (await c.GetAsync(r.Headers.Location!.OriginalString)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(evil, html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        var doc = await HttpHelpers.ParseAsync(await c.GetAsync("/accounts"));
        Assert.Empty(doc.QuerySelectorAll("script:not([src])"));
    }

    [SqlFact]
    public async Task Manager_can_open_team_records_and_a_user_without_a_team_sees_only_own()
    {
        var (f, c) = await AsAsync(ManagerA);
        using var _ = f;
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, (await c.GetAsync("/accounts")));
        var (f2, n) = await AsAsync("00000000-0000-4000-8000-00000000c001");
        using var _2 = f2;
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, (await n.GetAsync("/contacts")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, (await n.GetAsync("/opportunities")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, (await n.GetAsync("/activities")));
    }
}
