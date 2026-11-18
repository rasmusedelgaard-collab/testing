using System.Net;
using System.Net.Http.Headers;
using Crm.Web.Services;

namespace Crm.Tests.Http;

/// <summary>Increment 4 subset: general (name, accent, logo), visibility, users and teams. Admin-only by policy and by RLS.</summary>
[Collection("sql")]
public sealed class SettingsTests(SqlServerFixture fx)
{
    private const string Admin = "00000000-0000-4000-8000-00000000a001";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 0x1F, 0x15, 0xC4, 0x89];

    private async Task<(CrmFactory F, HttpClient C)> AsAsync(string oid)
    {
        var f = new CrmFactory(fx.AppDb);
        var c = HttpHelpers.NewClient(f);
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await HttpHelpers.SignInAsync(c, oid));
        return (f, c);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient c, byte[] bytes, string fileName = "logo.png", string? token = null)
    {
        token ??= await HttpHelpers.TokenAsync(c, "/settings/general");
        var content = new MultipartFormDataContent { { new StringContent(token), "_csrf" } };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "logo", fileName);
        return await c.PostAsync("/settings/general/logo", content);
    }

    [SqlTheory]
    [InlineData("00000000-0000-4000-8000-00000000a002")]
    [InlineData("00000000-0000-4000-8000-00000000a003")]
    public async Task Only_admins_can_open_or_post_settings_pages(string oid)
    {
        var (f, c) = await AsAsync(oid);
        using var guard = f;
        foreach (var path in new[] { "/settings", "/settings/general", "/settings/visibility", "/settings/users" })
        {
            var r = await c.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("You don't have access to this page.", (await HttpHelpers.ParseAsync(r)).QuerySelector("h1")!.TextContent);
        }
        var token = await HttpHelpers.TokenAsync(c, "/accounts/new");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/settings/general", HttpHelpers.Form(("_csrf", token), ("appName", "Hacked"), ("accentColor", "#000000")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/settings/visibility", HttpHelpers.Form(("_csrf", token), ("mode", "team"), ("confirm", "1")))).StatusCode);
    }

    [SqlFact]
    public async Task Settings_root_redirects_and_general_saves_name_and_accent_with_audit_and_theme()
    {
        var (f, c) = await AsAsync(Admin);
        using var guard = f;
        var root = await c.GetAsync("/settings");
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/settings/general", root.Headers.Location!.OriginalString);
        var bad = await c.PostAsync("/settings/general", HttpHelpers.Form(("_csrf", await HttpHelpers.TokenAsync(c, "/settings/general")), ("appName", ""), ("accentColor", "#FFFF00")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, bad);
        var badText = await bad.Content.ReadAsStringAsync();
        Assert.Contains("Enter a CRM name.", badText, StringComparison.Ordinal);
        Assert.Contains("This colour is too light for text and buttons. Choose a darker shade.", badText, StringComparison.Ordinal);
        Assert.Contains("Enter a colour like #1D4ED8.", await (await c.PostAsync("/settings/general", HttpHelpers.Form(("_csrf", await HttpHelpers.TokenAsync(c, "/settings/general")), ("appName", "X"), ("accentColor", "red")))).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        try
        {
            var ok = await c.PostAsync("/settings/general", HttpHelpers.Form(("_csrf", await HttpHelpers.TokenAsync(c, "/settings/general")), ("appName", "Acme CRM"), ("accentColor", "#0f766e")));
            await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, ok);
            var page = await HttpHelpers.ParseAsync(await c.GetAsync("/settings/general"));
            Assert.Equal("Settings saved.", page.QuerySelector(".flash")!.TextContent);
            Assert.Equal("#0F766E", page.QuerySelector("#f-accentColor")!.GetAttribute("value"));
            Assert.Contains("Acme CRM", page.QuerySelector("title")!.TextContent, StringComparison.Ordinal);
            var theme = await (await HttpHelpers.NewClient(f).GetAsync("/css/theme.css")).Content.ReadAsStringAsync();
            Assert.Contains("--color-primary: #0F766E", theme, StringComparison.Ordinal);
        }
        finally
        {
            await c.PostAsync("/settings/general", HttpHelpers.Form(("_csrf", await HttpHelpers.TokenAsync(c, "/settings/general")), ("appName", "CRM"), ("accentColor", "#1D4ED8")));
        }
    }

    [SqlFact]
    public async Task Logo_accepts_only_real_pngs_and_serves_them_with_a_locked_down_csp()
    {
        var (f, c) = await AsAsync(Admin);
        using var guard = f;
        var svg = await UploadAsync(c, "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray(), "logo.png");
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, svg);
        Assert.Contains("SVG logos are not supported. Upload a PNG, 500 KB or smaller.", await svg.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var text = await UploadAsync(c, "hello"u8.ToArray());
        Assert.Contains("Logo must be a PNG, 500 KB or smaller.", await text.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var tooBig = await UploadAsync(c, [.. Png, .. new byte[SettingsValidators.MaxLogoBytes]]);
        Assert.Contains("Logo must be a PNG, 500 KB or smaller.", await tooBig.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(c, Png, token: "forged")).StatusCode);
        var ok = await UploadAsync(c, Png, "evil.svg");
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, ok);
        var anon = HttpHelpers.NewClient(f);
        var logo = await anon.GetAsync("/branding/logo");
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, logo);
        Assert.Equal("image/png", logo.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await logo.Content.ReadAsByteArrayAsync());
        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; sandbox", string.Join(",", logo.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", string.Join(",", logo.Headers.GetValues("X-Content-Type-Options")));
        var etag = logo.Headers.ETag!.ToString();
        var req = new HttpRequestMessage(HttpMethod.Get, "/branding/logo");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await anon.SendAsync(req)).StatusCode);
        var signin = await HttpHelpers.ParseAsync(await anon.GetAsync("/signin"));
        Assert.StartsWith("/branding/logo?v=", signin.QuerySelector("img.logo")!.GetAttribute("src"), StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task Visibility_needs_a_confirmation_before_it_changes()
    {
        var (f, c) = await AsAsync(Admin);
        using var guard = f;
        var token = await HttpHelpers.TokenAsync(c, "/settings/visibility");
        var confirm = await c.PostAsync("/settings/visibility", HttpHelpers.Form(("_csrf", token), ("mode", "team")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, confirm);
        Assert.Contains("This changes what everyone can see immediately. Continue?", await confirm.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var bad = await c.PostAsync("/settings/visibility", HttpHelpers.Form(("_csrf", token), ("mode", "everyone")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Contains("Choose who can see records.", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var same = await c.PostAsync("/settings/visibility", HttpHelpers.Form(("_csrf", token), ("mode", "private")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, same); // unchanged: no confirmation needed
    }

    [SqlFact]
    public async Task Users_and_teams_page_shows_the_connection_line_and_manages_teams()
    {
        var (f, c) = await AsAsync(Admin);
        using var guard = f;
        var page = await c.GetAsync("/settings/users");
        await HttpHelpers.ExpectAsync(HttpStatusCode.OK, page);
        var doc = await HttpHelpers.ParseAsync(page);
        Assert.Matches(@"^Your connection: IP \S+ \(X-Forwarded-For hops trusted: [0-9]\)$", doc.QuerySelector("#connection-info")!.TextContent);
        Assert.Contains("Test Admin", doc.QuerySelectorAll("table.data").Last().TextContent, StringComparison.Ordinal);
        var filtered = await HttpHelpers.ParseAsync(await c.GetAsync("/settings/users?role=Manager&q=manager"));
        Assert.DoesNotContain("Test Admin", filtered.QuerySelectorAll("table.data").Last().TextContent, StringComparison.Ordinal);

        var group = Guid.NewGuid().ToString();
        var name = "Team-" + group[..8];
        var token = await HttpHelpers.TokenAsync(c, "/settings/users");
        var invalid = await c.PostAsync("/settings/teams", HttpHelpers.Form(("_csrf", token), ("name", ""), ("groupId", "nope")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, invalid);
        var text = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("Enter a team name.", text, StringComparison.Ordinal);
        Assert.Contains("Enter the group object ID in this format: 00000000-0000-0000-0000-000000000000.", text, StringComparison.Ordinal);
        var added = await c.PostAsync("/settings/teams", HttpHelpers.Form(("_csrf", token), ("name", name), ("groupId", "{" + group.ToUpperInvariant() + "}")));
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, added);
        var after = await HttpHelpers.ParseAsync(await c.GetAsync("/settings/users"));
        Assert.Equal($"Team {name} added.", after.QuerySelector(".flash")!.TextContent);
        var dup = await c.PostAsync("/settings/teams", HttpHelpers.Form(("_csrf", token), ("name", name.ToUpperInvariant()), ("groupId", Guid.NewGuid().ToString())));
        await HttpHelpers.ExpectAsync(HttpStatusCode.UnprocessableEntity, dup);
        Assert.Contains($"A team named {name} already exists.", await dup.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var dupGroup = await c.PostAsync("/settings/teams", HttpHelpers.Form(("_csrf", token), ("name", "Other-" + name), ("groupId", group)));
        Assert.Contains($"This group is already linked to the team {name}.", await dupGroup.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var link = after.QuerySelectorAll("a").First(a => a.TextContent == $"Edit {name}").GetAttribute("href")!;
        var id = link.Split('/')[3];
        var edit = await HttpHelpers.ParseAsync(await c.GetAsync(link));
        Assert.Equal(name, edit.QuerySelector("#f-name")!.GetAttribute("value"));
        var changeGroup = await c.PostAsync($"/settings/teams/{id}", HttpHelpers.Form(("_csrf", token), ("name", name), ("groupId", Guid.NewGuid().ToString())));
        Assert.Contains("Changing the group changes who is in", await changeGroup.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var deact = await c.GetAsync($"/settings/teams/{id}/deactivate");
        Assert.Contains("will have no team from their next sign-in", await deact.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await c.PostAsync($"/settings/teams/{id}/deactivate", HttpHelpers.Form(("_csrf", token))));
        var inactive = await HttpHelpers.ParseAsync(await c.GetAsync("/settings/users"));
        Assert.Contains("Reactivate " + name, inactive.QuerySelector("body")!.TextContent, StringComparison.Ordinal);
        await HttpHelpers.ExpectAsync(HttpStatusCode.SeeOther, await c.PostAsync($"/settings/teams/{id}/reactivate", HttpHelpers.Form(("_csrf", token))));
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/settings/teams/2147483647/deactivate", HttpHelpers.Form(("_csrf", token)))).StatusCode);
    }
}
