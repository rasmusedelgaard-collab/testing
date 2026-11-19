using System.Net;

namespace Crm.Tests.Http;

/// <summary>New routes and shell behaviour of the UI redesign that need no database.</summary>
public sealed class RedesignNoSqlTests : IDisposable
{
    private readonly CrmFactory _f = new();

    public void Dispose() => _f.Dispose();

    [Theory]
    [InlineData("/pipeline", "/signin?returnTo=%2Fpipeline")]
    [InlineData("/search?q=abc", "/signin?returnTo=%2Fsearch%3Fq%3Dabc")]
    public async Task New_pages_need_a_session(string path, string expected)
    {
        var r = await HttpHelpers.NewClient(_f).GetAsync(path);
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal(expected, r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Stage_endpoint_needs_a_session_and_has_no_get()
    {
        var c = HttpHelpers.NewClient(_f);
        var post = await c.PostAsync("/opportunities/5/stage", HttpHelpers.Form(("stageId", "1")));
        Assert.Equal(HttpStatusCode.SeeOther, post.StatusCode);
        Assert.Equal("/signin", post.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.SeeOther, (await c.GetAsync("/opportunities/5/stage")).StatusCode);
        // the crmid constraint keeps junk ids away from the handler
        Assert.Equal(HttpStatusCode.SeeOther, (await c.PostAsync("/opportunities/abc/stage", HttpHelpers.Form(("stageId", "1")))).StatusCode);
    }

    [Fact]
    public async Task Theme_post_sets_a_lax_preference_cookie_and_goes_back_to_a_local_path()
    {
        var c = HttpHelpers.NewClient(_f);
        var token = await HttpHelpers.TokenAsync(c, "/auth/test-login");
        var r = await c.PostAsync("/theme", HttpHelpers.Form(("_csrf", token), ("theme", "dark"), ("returnTo", "/accounts?q=a")));
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/accounts?q=a", r.Headers.Location!.OriginalString);
        var cookie = r.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("theme=", StringComparison.Ordinal));
        Assert.StartsWith("theme=dark", cookie, StringComparison.Ordinal);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        // an open redirect falls back to the dashboard
        var evil = await c.PostAsync("/theme", HttpHelpers.Form(("_csrf", token), ("theme", "light"), ("returnTo", "//evil.example")));
        Assert.Equal("/", evil.Headers.Location!.OriginalString);
        // "system" removes the cookie; an unknown value changes nothing
        var system = await c.PostAsync("/theme", HttpHelpers.Form(("_csrf", token), ("theme", "system")));
        Assert.Contains("expires=Thu, 01 Jan 1970", string.Join(";", system.Headers.GetValues("Set-Cookie").Where(h => h.StartsWith("theme=", StringComparison.Ordinal))), StringComparison.OrdinalIgnoreCase);
        var junk = await c.PostAsync("/theme", HttpHelpers.Form(("_csrf", token), ("theme", "<script>")));
        Assert.DoesNotContain(junk.Headers.TryGetValues("Set-Cookie", out var set) ? set : [], h => h.StartsWith("theme=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Theme_post_without_an_antiforgery_token_is_refused_and_get_is_not_found()
    {
        var c = HttpHelpers.NewClient(_f);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/theme", HttpHelpers.Form(("theme", "dark")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/theme")).StatusCode);
    }

    [Fact]
    public async Task The_theme_cookie_is_rendered_server_side_as_data_theme()
    {
        var c = HttpHelpers.NewClient(_f);
        var plain = await HttpHelpers.ParseAsync(await c.GetAsync("/signin"));
        Assert.False(plain.DocumentElement.HasAttribute("data-theme"));
        foreach (var choice in new[] { "light", "dark" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/signin");
            req.Headers.Add("Cookie", $"theme={choice}");
            Assert.Equal(choice, (await HttpHelpers.ParseAsync(await c.SendAsync(req))).DocumentElement.GetAttribute("data-theme"));
        }
        var bad = new HttpRequestMessage(HttpMethod.Get, "/signin");
        bad.Headers.Add("Cookie", "theme=purple");
        Assert.False((await HttpHelpers.ParseAsync(await c.SendAsync(bad))).DocumentElement.HasAttribute("data-theme"));
    }

    [Fact]
    public async Task Signin_uses_the_split_brand_layout_without_the_app_shell()
    {
        var doc = await HttpHelpers.ParseAsync(await HttpHelpers.NewClient(_f).GetAsync("/signin"));
        Assert.Null(doc.QuerySelector("nav.sidebar"));
        Assert.Null(doc.QuerySelector("header.topbar"));
        Assert.Contains("Close to the markets – close to you", doc.QuerySelector(".signin-brand .tagline")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("/img/brand/dafa-logo-negative.svg", doc.QuerySelector(".signin-brand img.logo-negative")!.GetAttribute("src"));
        Assert.Equal("Sign in", doc.QuerySelector("h1")!.TextContent);
        Assert.NotNull(doc.QuerySelector("main#main"));
        Assert.StartsWith("©", doc.QuerySelector(".copyright")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Brand_files_icon_sprite_and_scripts_are_static_files()
    {
        var c = HttpHelpers.NewClient(_f);
        foreach (var path in new[] { "/img/brand/dafa-logo-primary-blue.svg", "/img/brand/dafa-logo-negative.svg", "/img/brand/dafa-rings-gray.svg", "/img/brand/dafa-rings-white.svg", "/img/brand/icon-wind-gray.svg", "/img/ui/icons.svg", "/img/ui/chevron-down-light.svg", "/img/ui/chevron-down-dark.svg" })
        {
            var r = await c.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("image/svg+xml", r.Content.Headers.ContentType!.MediaType);
        }
        var app = await c.GetStringAsync("/js/app.js");
        Assert.Contains("role", app, StringComparison.Ordinal);
        Assert.Contains("combobox", app, StringComparison.Ordinal);
        Assert.Contains("crm.sidebar", await c.GetStringAsync("/js/boot.js"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Charts_read_their_colours_from_the_css_variables()
    {
        var charts = await HttpHelpers.NewClient(_f).GetStringAsync("/js/charts.js");
        Assert.Contains("--chart-1", charts, StringComparison.Ordinal);
        Assert.Contains("--chart-grid", charts, StringComparison.Ordinal);
        Assert.Contains("prefers-color-scheme: dark", charts, StringComparison.Ordinal);
        Assert.DoesNotMatch("#[0-9A-Fa-f]{6}", charts);
    }
}
