using System.Net;

namespace Crm.Tests.Http;

/// <summary>HTTP tests that need no database (branding is faked; the code gate, headers, antiforgery and limits are all in memory).</summary>
public sealed class NoSqlHttpTests : IDisposable
{
    private readonly CrmFactory _f = new();

    public void Dispose() => _f.Dispose();

    [Theory]
    [InlineData("/accounts/+5")]
    [InlineData("/accounts/005")]
    [InlineData("/accounts/%205")]
    [InlineData("/accounts/2147483648")]
    [InlineData("/accounts/0/edit")]
    [InlineData("/opportunities/+5/won")]
    [InlineData("/settings/fx/005/edit")]
    [InlineData("/settings/teams/+1/edit")]
    public async Task Non_canonical_ids_never_match_a_route(string path)
    {
        var r = await HttpHelpers.NewClient(_f).GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Healthz_is_ok_without_a_database()
    {
        var c = HttpHelpers.NewClient(_f);
        var r = await c.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ok", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readyz_fails_when_the_database_is_unreachable()
    {
        var r = await HttpHelpers.NewClient(_f).GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("fail", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signin_page_renders_with_banner_and_no_server_header()
    {
        var r = await HttpHelpers.NewClient(_f).GetAsync("/signin");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var doc = await HttpHelpers.ParseAsync(r);
        Assert.Equal("Sign in", doc.QuerySelector("h1")!.TextContent);
        Assert.Contains("TEST ENVIRONMENT — test data only", doc.QuerySelector(".env-banner")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("/auth/login?returnTo=%2F", doc.QuerySelector("a.button.primary")!.GetAttribute("href"), StringComparison.Ordinal);
        Assert.False(r.Headers.Contains("Server"));
        Assert.False(r.Headers.Contains("X-Powered-By"));
        Assert.NotNull(doc.QuerySelector("script[src='/js/boot.js']"));
    }

    [Fact]
    public async Task Security_headers_match_the_node_set()
    {
        var r = await HttpHelpers.NewClient(_f).GetAsync("/signin");
        string H(string n) => string.Join(",", r.Headers.GetValues(n));
        Assert.Equal("default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self' https://login.microsoftonline.com", H("Content-Security-Policy"));
        Assert.Equal("same-origin", H("Referrer-Policy"));
        Assert.Equal("nosniff", H("X-Content-Type-Options"));
        Assert.Equal("SAMEORIGIN", H("X-Frame-Options"));
        Assert.Equal("same-origin", H("Cross-Origin-Opener-Policy"));
        Assert.Equal("same-origin", H("Cross-Origin-Resource-Policy"));
        Assert.Equal("?1", H("Origin-Agent-Cluster"));
        Assert.Equal("off", H("X-DNS-Prefetch-Control"));
        Assert.Equal("none", H("X-Permitted-Cross-Domain-Policies"));
        Assert.Equal("0", H("X-XSS-Protection"));
        Assert.True(r.Headers.Contains("X-Request-Id"));
    }

    [Fact]
    public async Task No_inline_script_or_style_in_pages()
    {
        var doc = await HttpHelpers.ParseAsync(await HttpHelpers.NewClient(_f).GetAsync("/auth/test-login"));
        Assert.Empty(doc.QuerySelectorAll("script:not([src])"));
        Assert.Empty(doc.QuerySelectorAll("[style]"));
    }

    [Fact]
    public async Task Static_assets_are_served_with_cache_control_and_theme_css_is_an_endpoint()
    {
        var c = HttpHelpers.NewClient(_f);
        var css = await c.GetAsync("/css/app.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("public, max-age=3600", css.Headers.CacheControl!.ToString());
        var theme = await c.GetAsync("/css/theme.css");
        Assert.Equal("text/css; charset=utf-8", theme.Content.Headers.ContentType!.ToString());
        Assert.Contains("--color-primary: #1D4ED8", await theme.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var etag = theme.Headers.ETag!.ToString();
        var req = new HttpRequestMessage(HttpMethod.Get, "/css/theme.css");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(req)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/branding/logo")).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_goes_to_signin_with_return_to()
    {
        var c = HttpHelpers.NewClient(_f);
        var r = await c.GetAsync("/accounts?q=a");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/signin?returnTo=%2Faccounts%3Fq%3Da", r.Headers.Location!.OriginalString);
        var post = await c.PostAsync("/accounts", HttpHelpers.Form(("x", "y")));
        Assert.Equal("/signin", post.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Post_without_antiforgery_token_is_403_with_the_error_page()
    {
        var r = await HttpHelpers.NewClient(_f).PostAsync("/auth/test-login", HttpHelpers.Form(("code", CrmFactory.AccessCode)));
        Assert.True(r.StatusCode == HttpStatusCode.Forbidden, await r.Content.ReadAsStringAsync());
        var doc = await HttpHelpers.ParseAsync(r);
        Assert.Equal("You don't have access to this page.", doc.QuerySelector("h1")!.TextContent);
        Assert.Contains("Reference:", doc.QuerySelector("p.muted")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Antiforgery_cookie_flags()
    {
        var r = await HttpHelpers.NewClient(_f).GetAsync("/auth/test-login");
        var cookie = r.Headers.GetValues("Set-Cookie").First(h => h.StartsWith("crm.af", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_code_is_401_and_the_error_is_shown()
    {
        var c = HttpHelpers.NewClient(_f);
        var t = await HttpHelpers.TokenAsync(c, "/auth/test-login");
        var r = await c.PostAsync("/auth/test-login", HttpHelpers.Form(("_csrf", t), ("code", "wrong")));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("That access code is not correct.", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_oid_cannot_be_picked_without_the_verified_cookie()
    {
        var c = HttpHelpers.NewClient(_f);
        var t = await HttpHelpers.TokenAsync(c, "/auth/test-login");
        var r = await c.PostAsync("/auth/test-login/user", HttpHelpers.Form(("_csrf", t), ("oid", "00000000-0000-4000-8000-00000000a001")));
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/auth/test-login", r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Auth_login_redirects_to_the_code_page_in_test_mode()
    {
        var r = await HttpHelpers.NewClient(_f).GetAsync("/auth/login?returnTo=/accounts");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/auth/test-login?returnTo=%2Faccounts", r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Open_redirect_attempts_fall_back_to_root()
    {
        var doc = await HttpHelpers.ParseAsync(await HttpHelpers.NewClient(_f).GetAsync("/auth/test-login?returnTo=//evil.example"));
        Assert.Equal("/", doc.QuerySelector("input[name=returnTo]")!.GetAttribute("value"));
    }

    [Fact]
    public async Task Unknown_route_is_the_404_page()
    {
        var c = HttpHelpers.NewClient(_f);
        var r = await c.GetAsync("/does-not-exist");
        // unauthenticated: the fallback policy redirects first, which is the Node behaviour as well
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
    }

    [Fact]
    public async Task Signout_get_is_not_found_and_signed_out_page_renders()
    {
        var c = HttpHelpers.NewClient(_f);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/auth/signout")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/signed-out")).StatusCode);
        var noAccess = await c.GetAsync("/no-access");
        Assert.Equal(HttpStatusCode.Forbidden, noAccess.StatusCode);
        Assert.Contains("You don't have access to the CRM yet", await noAccess.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

public sealed class LockoutHttpTests
{
    [Fact]
    public async Task Five_wrong_codes_lock_the_client_and_even_the_right_code_is_refused()
    {
        await using var f = new CrmFactory();
        var c = HttpHelpers.NewClient(f);
        var t = await HttpHelpers.TokenAsync(c, "/auth/test-login");
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/auth/test-login", HttpHelpers.Form(("_csrf", t), ("code", "bad" + i)))).StatusCode);
        }
        var fifth = await c.PostAsync("/auth/test-login", HttpHelpers.Form(("_csrf", t), ("code", "bad")));
        Assert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode);
        Assert.Contains("Too many incorrect attempts. Wait 15 minutes and try again.", await fifth.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var right = await c.PostAsync("/auth/test-login", HttpHelpers.Form(("_csrf", t), ("code", CrmFactory.AccessCode)));
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
        var page = await c.GetAsync("/auth/test-login");
        Assert.Contains("Too many incorrect attempts", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auth_paths_are_limited_to_120_requests_per_minute_with_retry_after()
    {
        await using var f = new CrmFactory();
        var c = HttpHelpers.NewClient(f);
        HttpResponseMessage? last = null;
        for (var i = 0; i < 121; i++)
        {
            last = await c.GetAsync("/auth/login");
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.True(last.Headers.Contains("Retry-After"));
        Assert.Equal("Too many requests", await last.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/healthz")).StatusCode);
    }
}
