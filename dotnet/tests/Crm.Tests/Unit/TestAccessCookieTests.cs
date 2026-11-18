using Crm.Web.Auth;
using Crm.Web.Config;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;

namespace Crm.Tests.Unit;

public class TestAccessCookieTests
{
    private static CrmConfig Config() => CrmConfigLoader.Load(k => k switch
    {
        "ASPNETCORE_ENVIRONMENT" => "Production",
        "AUTH_MODE" => "test-access-code",
        "REDIRECT_URI" => "https://crmtest.example.com/auth/redirect",
        "TEST_ACCESS_CODE" => "test-access-code-abcdef-123456",
        "DB_SERVER" => "db.example.com",
        "DB_NAME" => "db1",
        "DB_AUTH_MODE" => "sql-password",
        "DB_USER" => "owner",
        "DB_PASSWORD" => "pw",
        _ => null,
    });

    private static (TestAccessCookie Cookie, FakeTimeProvider Time) Make()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        return (new TestAccessCookie(new EphemeralDataProtectionProvider(), Config(), time), time);
    }

    private static string IssueValue(TestAccessCookie cookie)
    {
        var ctx = new DefaultHttpContext();
        cookie.Issue(ctx);
        var header = ctx.Response.Headers.SetCookie.ToString();
        Assert.StartsWith("__Host-crm.tac=", header, StringComparison.Ordinal);
        Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
        return header.Split(';')[0]["__Host-crm.tac=".Length..];
    }

    private static DefaultHttpContext WithCookie(string value)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"__Host-crm.tac={value}";
        return ctx;
    }

    [Fact]
    public void A_cookie_can_be_consumed_once_and_a_replay_is_refused_while_viewing_stays_possible()
    {
        var (cookie, _) = Make();
        var v = IssueValue(cookie);
        Assert.True(cookie.IsVerified(WithCookie(v)));
        Assert.True(cookie.IsVerified(WithCookie(v))); // the picker page may be reloaded
        Assert.True(cookie.TryConsume(WithCookie(v)));
        Assert.False(cookie.TryConsume(WithCookie(v))); // replay
    }

    [Fact]
    public void Every_issued_cookie_is_unique_and_independent()
    {
        var (cookie, _) = Make();
        var a = IssueValue(cookie);
        var b = IssueValue(cookie);
        Assert.NotEqual(a, b);
        Assert.True(cookie.TryConsume(WithCookie(a)));
        Assert.True(cookie.TryConsume(WithCookie(b)));
    }

    [Fact]
    public void Expired_tampered_and_missing_cookies_are_refused()
    {
        var (cookie, time) = Make();
        var v = IssueValue(cookie);
        Assert.False(cookie.TryConsume(WithCookie(v + "x")));
        Assert.False(cookie.TryConsume(new DefaultHttpContext()));
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(cookie.IsVerified(WithCookie(v)));
        Assert.False(cookie.TryConsume(WithCookie(v)));
    }
}
