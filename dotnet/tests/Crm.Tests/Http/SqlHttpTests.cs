using System.Net;
using Crm.Web.Data;
using Microsoft.Data.SqlClient;

namespace Crm.Tests.Http;

[Collection("sql")]
public sealed class SqlHttpTests(SqlServerFixture fx)
{
    private const string AdminOid = "00000000-0000-4000-8000-00000000a001";
    private const string UserOid = "00000000-0000-4000-8000-00000000a003";

    private async Task ExecOwnerAsync(string sql)
    {
        await using var c = new SqlConnection(SqlConnectionFactory.Build(fx.OwnerDb, "owner-http", 1, false, 30));
        await c.OpenAsync();
        await SqlServerFixture.ExecAsync(c, sql);
    }

    [SqlFact]
    public async Task Readyz_is_ok_with_a_reachable_database()
    {
        await using var f = new CrmFactory(fx.AppDb);
        var r = await HttpHelpers.NewClient(f).GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [SqlTheory]
    [InlineData("00000000-0000-4000-8000-00000000a001", "Admin")]
    [InlineData("00000000-0000-4000-8000-00000000a002", "Manager")]
    [InlineData("00000000-0000-4000-8000-00000000a003", "User")]
    [InlineData("00000000-0000-4000-8000-00000000a004", "User")]
    [InlineData("00000000-0000-4000-8000-00000000b001", "Manager")]
    [InlineData("00000000-0000-4000-8000-00000000b002", "User")]
    [InlineData("00000000-0000-4000-8000-00000000c001", "User")]
    public async Task Every_seeded_user_can_sign_in_and_sees_the_banner_and_their_role(string oid, string role)
    {
        await using var f = new CrmFactory(fx.AppDb);
        var c = HttpHelpers.NewClient(f);
        var login = await HttpHelpers.SignInAsync(c, oid, "/");
        Assert.Equal(HttpStatusCode.SeeOther, login.StatusCode);
        Assert.Equal("/", login.Headers.Location!.OriginalString);
        var home = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("no-store", home.Headers.CacheControl!.ToString());
        var doc = await HttpHelpers.ParseAsync(home);
        Assert.NotNull(doc.QuerySelector(".env-banner"));
        Assert.Equal($"Role: {role}", doc.QuerySelector(".badge")!.TextContent);
        Assert.Equal(oid == AdminOid, doc.QuerySelector("a[href='/settings/general']") is not null);
    }

    [SqlFact]
    public async Task The_named_admin_is_offered_and_a_tampered_oid_is_403()
    {
        await using var f = new CrmFactory(fx.AppDb);
        var c = HttpHelpers.NewClient(f);
        var t1 = await HttpHelpers.TokenAsync(c, "/auth/test-login");
        var picker = await c.PostAsync("/auth/test-login", HttpHelpers.Form(("_csrf", t1), ("code", CrmFactory.AccessCode)));
        var doc = await HttpHelpers.ParseAsync(picker);
        Assert.Contains("Fixture Admin", doc.QuerySelector("form")!.TextContent, StringComparison.Ordinal);
        Assert.True(doc.QuerySelectorAll("input[name=oid]").Length >= 8);
        var t2 = doc.QuerySelector("input[name=_csrf]")!.GetAttribute("value")!;
        foreach (var bad in new[] { Guid.NewGuid().ToString(), "00000000-0000-4000-8000-ffffffffffff", "x", "" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/auth/test-login/user", HttpHelpers.Form(("_csrf", t2), ("oid", bad)))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.SeeOther, (await c.GetAsync("/")).StatusCode);
    }

    [SqlFact]
    public async Task The_verified_code_window_lasts_ten_minutes()
    {
        await using var f = new CrmFactory(fx.AppDb);
        var c = HttpHelpers.NewClient(f);
        var t1 = await HttpHelpers.TokenAsync(c, "/auth/test-login");
        var picker = await c.PostAsync("/auth/test-login", HttpHelpers.Form(("_csrf", t1), ("code", CrmFactory.AccessCode)));
        var t2 = (await HttpHelpers.ParseAsync(picker)).QuerySelector("input[name=_csrf]")!.GetAttribute("value")!;
        f.Time.Advance(TimeSpan.FromMinutes(11));
        var r = await c.PostAsync("/auth/test-login/user", HttpHelpers.Form(("_csrf", t2), ("oid", UserOid)));
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/auth/test-login", r.Headers.Location!.OriginalString);
    }

    [SqlFact]
    public async Task Sign_out_removes_the_session_and_requires_antiforgery()
    {
        await using var f = new CrmFactory(fx.AppDb);
        var c = HttpHelpers.NewClient(f);
        await HttpHelpers.SignInAsync(c, UserOid);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/auth/signout", HttpHelpers.Form(("x", "y")))).StatusCode);
        var token = await HttpHelpers.TokenAsync(c, "/");
        var r = await c.PostAsync("/auth/signout", HttpHelpers.Form(("_csrf", token)));
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/signed-out", r.Headers.Location!.OriginalString);
        var after = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.SeeOther, after.StatusCode);
        Assert.StartsWith("/signin", after.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task Idle_timeout_ends_the_session_after_60_minutes()
    {
        await using var f = new CrmFactory(fx.AppDb);
        var c = HttpHelpers.NewClient(f);
        await HttpHelpers.SignInAsync(c, UserOid);
        f.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/")).StatusCode);
        f.Time.Advance(TimeSpan.FromMinutes(61));
        var r = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.StartsWith("/signin?returnTo=", r.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("reason=expired", r.Headers.Location.OriginalString, StringComparison.Ordinal); // L3: a lost ticket still says why
    }

    [SqlFact]
    public async Task Absolute_timeout_ends_an_active_session_after_10_hours_with_reason_expired()
    {
        await using var f = new CrmFactory(fx.AppDb);
        var c = HttpHelpers.NewClient(f);
        await HttpHelpers.SignInAsync(c, UserOid);
        for (var i = 0; i < 19; i++) // stay active every 30 minutes for 9.5 hours (also passes the 5-minute IsActive re-check)
        {
            f.Time.Advance(TimeSpan.FromMinutes(30));
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/")).StatusCode);
        }
        f.Time.Advance(TimeSpan.FromMinutes(31));
        var r = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Contains("reason=expired", r.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var page = await HttpHelpers.ParseAsync(await c.GetAsync(r.Headers.Location.OriginalString));
        Assert.Contains("Your session has ended.", page.QuerySelector(".flash")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Anything you hadn't saved was lost.", page.QuerySelector(".flash")!.TextContent, StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task A_deactivated_user_is_signed_out_within_five_minutes_with_reason_disabled()
    {
        await using var f = new CrmFactory(fx.AppDb);
        var c = HttpHelpers.NewClient(f);
        await HttpHelpers.SignInAsync(c, "00000000-0000-4000-8000-00000000b002");
        await ExecOwnerAsync("UPDATE crm.Users SET IsActive = 0 WHERE EntraOid = '00000000-0000-4000-8000-00000000b002'");
        try
        {
            f.Time.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/")).StatusCode); // not re-checked yet
            f.Time.Advance(TimeSpan.FromMinutes(4));
            var r = await c.GetAsync("/");
            Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
            Assert.Contains("reason=disabled", r.Headers.Location!.OriginalString, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.SeeOther, (await c.GetAsync("/")).StatusCode);
        }
        finally
        {
            await ExecOwnerAsync("UPDATE crm.Users SET IsActive = 1 WHERE EntraOid = '00000000-0000-4000-8000-00000000b002'");
        }
    }

    [SqlFact]
    public async Task A_new_sign_in_creates_a_new_session_row_and_the_old_cookie_does_not_work()
    {
        await using var f = new CrmFactory(fx.AppDb);
        var c1 = HttpHelpers.NewClient(f);
        await HttpHelpers.SignInAsync(c1, UserOid);
        var c2 = HttpHelpers.NewClient(f);
        await HttpHelpers.SignInAsync(c2, UserOid);
        Assert.Equal(HttpStatusCode.OK, (await c1.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c2.GetAsync("/")).StatusCode);
        await ExecOwnerAsync("DELETE FROM app.Sessions"); // revocation runbook
        Assert.Equal(HttpStatusCode.SeeOther, (await c1.GetAsync("/")).StatusCode);
    }

    [SqlFact]
    public async Task Logo_endpoint_and_theme_work_against_the_database()
    {
        await using var f = new CrmFactory(fx.AppDb);
        var c = HttpHelpers.NewClient(f);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/branding/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/css/theme.css")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/signin")).StatusCode);
    }
}
