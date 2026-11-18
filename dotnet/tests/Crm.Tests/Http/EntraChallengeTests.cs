using System.Net;
using System.Web;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Crm.Tests.Http;

public sealed class EntraChallengeTests
{
    private const string Tenant = "11111111-2222-4333-8444-555555555555";
    private const string Client = "66666666-7777-4888-8999-000000000000";
    private const string Redirect = "http://localhost/auth/redirect";

    private sealed class EntraFactory : WebApplicationFactory<Program>
    {
        public List<string> Errors { get; } = [];

        private sealed class Capture(List<string> sink) : ILoggerProvider, ILogger
        {
            public ILogger CreateLogger(string categoryName) => this;
            public void Dispose() { }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => sink.Add(formatter(state, exception) + " " + exception);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(l => l.AddProvider(new Capture(Errors)));
            foreach (var (k, v) in new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Testing",
                ["AUTH_MODE"] = "entra",
                ["ENTRA_TENANT_ID"] = Tenant,
                ["ENTRA_CLIENT_ID"] = Client,
                ["ENTRA_CLIENT_SECRET"] = "not-a-real-secret",
                ["REDIRECT_URI"] = Redirect,
                ["DB_AUTH_MODE"] = "sql-password",
                ["DB_CONNECTION_STRING"] = "Server=127.0.0.1,1;Database=none;User Id=u;Password=p",
                ["DATA_PROTECTION_KEYS_PATH"] = Path.Combine(Path.GetTempPath(), "crm-test-keys-" + Guid.NewGuid().ToString("N")),
            })
            {
                builder.UseSetting(k, v);
            }
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton<Crm.Web.Data.BrandingCache, FakeBranding>();
                // No network: a static OpenID configuration instead of the metadata document.
                s.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                {
                    var configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
                        AuthorizationEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize",
                        TokenEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
                    };
                    o.Configuration = configuration;
                    o.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
            });
        }
    }

    [Fact]
    public async Task Login_redirects_to_the_authorize_endpoint_with_query_mode_pkce_and_the_configured_redirect_uri()
    {
        await using var f = new EntraFactory();
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var r = await c.GetAsync("/auth/login?returnTo=/accounts");
        Assert.True(r.StatusCode == HttpStatusCode.Redirect, string.Join("\n", f.Errors));
        var url = r.Headers.Location!;
        Assert.StartsWith($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize", url.AbsoluteUri, StringComparison.Ordinal);
        var q = HttpUtility.ParseQueryString(url.Query);
        Assert.True("query" == q["response_mode"], url.AbsoluteUri);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(q["code_challenge"]));
        Assert.Equal(Redirect, q["redirect_uri"]);
        Assert.Equal(Client, q["client_id"]);
        Assert.Contains("openid", q["scope"], StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(q["state"]));
        Assert.False(string.IsNullOrEmpty(q["nonce"]));
        var cookies = r.Headers.GetValues("Set-Cookie").ToList();
        Assert.All(cookies.Where(x => x.Contains("correlation", StringComparison.OrdinalIgnoreCase) || x.Contains("nonce", StringComparison.OrdinalIgnoreCase)),
            x => Assert.Contains("samesite=lax", x, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unauthenticated_requests_go_to_the_signin_page_first_and_signin_links_to_auth_login()
    {
        await using var f = new EntraFactory();
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var r = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.StartsWith("/signin", r.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var page = await HttpHelpers.ParseAsync(await c.GetAsync("/signin"));
        Assert.Null(page.QuerySelector(".env-banner"));
    }

    [Fact]
    public async Task The_oidc_callback_without_state_shows_the_sign_in_problem_page_with_a_reference()
    {
        await using var f = new EntraFactory();
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var r = await c.GetAsync("/auth/redirect?code=abc&state=bogus");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        var page = await c.GetAsync(r.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.BadRequest, page.StatusCode);
        var doc = await HttpHelpers.ParseAsync(page);
        Assert.Equal("We couldn't sign you in", doc.QuerySelector("h1")!.TextContent);
        Assert.Contains("Reference:", doc.QuerySelector("p.muted")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_test_login_pages_do_not_exist_in_entra_mode()
    {
        await using var f = new EntraFactory();
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/auth/test-login")).StatusCode);
    }
}
