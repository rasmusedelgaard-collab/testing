using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Crm.Tests.Http;

public static class HttpHelpers
{
    public static HttpClient NewClient(CrmFactory f) =>
        f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public static async Task<IHtmlDocument> ParseAsync(HttpResponseMessage r) =>
        (IHtmlDocument)await new HtmlParser().ParseDocumentAsync(await r.Content.ReadAsStringAsync());

    /// <summary>GET a page and return the antiforgery token from its first form.</summary>
    public static async Task<string> TokenAsync(HttpClient c, string path)
    {
        var r = await c.GetAsync(path);
        var doc = await ParseAsync(r);
        return doc.QuerySelector("input[name=_csrf]")?.GetAttribute("value") ?? throw new InvalidOperationException($"No antiforgery token on {path} ({(int)r.StatusCode})");
    }

    private static readonly System.Text.RegularExpressions.Regex Tags = new("<[^>]+>|\\s+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Asserts the status and, on mismatch, shows the page text so the cause is visible in CI logs.</summary>
    public static async Task ExpectAsync(HttpStatusCode expected, HttpResponseMessage r)
    {
        if (r.StatusCode != expected)
        {
            var text = Tags.Replace(await r.Content.ReadAsStringAsync(), " ");
            Assert.Fail($"Expected {expected} but got {(int)r.StatusCode} {r.StatusCode} for {r.RequestMessage?.Method} {r.RequestMessage?.RequestUri?.PathAndQuery} location={r.Headers.Location}: {(text.Length > 400 ? text[..400] : text)} | server log: {string.Join(" // ", TestOutputLoggerProvider.Recent.Where(l => !l.Contains("AUTH_MODE", StringComparison.Ordinal) && !l.Contains("XML encryptor", StringComparison.Ordinal)).TakeLast(4))}");
        }
    }

    public static FormUrlEncodedContent Form(params (string, string)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));

    /// <summary>The real test-access-code flow: GET code page, POST code with token, POST user with token.</summary>
    public static async Task<HttpResponseMessage> SignInAsync(HttpClient c, string oid, string returnTo = "/")
    {
        var t1 = await TokenAsync(c, "/auth/test-login");
        var r1 = await c.PostAsync("/auth/test-login", Form(("_csrf", t1), ("code", CrmFactory.AccessCode), ("returnTo", returnTo)));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var doc = await ParseAsync(r1);
        var t2 = doc.QuerySelector("input[name=_csrf]")!.GetAttribute("value")!;
        return await c.PostAsync("/auth/test-login/user", Form(("_csrf", t2), ("oid", oid), ("returnTo", returnTo)));
    }
}
