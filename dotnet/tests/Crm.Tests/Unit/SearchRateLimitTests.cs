using System.Net;
using System.Threading.RateLimiting;
using Crm.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace Crm.Tests.Unit;

/// <summary>L-1: /search is limited to 60 requests per minute per client IP; other pages are not affected.</summary>
public sealed class SearchRateLimitTests
{
    private static DefaultHttpContext Request(string path, string ip)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return ctx;
    }

    private static PartitionedRateLimiter<HttpContext> Limiter()
    {
        var options = new RateLimiterOptions();
        RateLimitSetup.Configure(options);
        return options.GlobalLimiter!;
    }

    [Fact]
    public void The_sixty_first_search_in_a_minute_is_rejected_per_ip()
    {
        using var limiter = Limiter();
        for (var i = 0; i < 60; i++)
        {
            using var lease = limiter.AttemptAcquire(Request("/search", "203.0.113.5"));
            Assert.True(lease.IsAcquired, $"request {i + 1}");
        }
        using var rejected = limiter.AttemptAcquire(Request("/search", "203.0.113.5"));
        Assert.False(rejected.IsAcquired);
        // Another client is not affected, and neither are other pages of the same client.
        using var other = limiter.AttemptAcquire(Request("/search", "203.0.113.6"));
        Assert.True(other.IsAcquired);
        for (var i = 0; i < 200; i++)
        {
            using var page = limiter.AttemptAcquire(Request("/opportunities", "203.0.113.5"));
            Assert.True(page.IsAcquired);
        }
    }

    [Theory]
    [InlineData("/search", "search", 60)]
    [InlineData("/Search", "search", 60)]
    [InlineData("/auth/login", "auth", 120)]
    [InlineData("/readyz", "readyz", 30)]
    [InlineData("/accounts", "", 0)]
    public void Policies_are_chosen_by_path(string path, string policy, int limit)
    {
        Assert.Equal((policy, limit), RateLimitSetup.PolicyFor(path));
    }
}

/// <summary>L-4: the theme toggle never returns to a URL that only accepts POST.</summary>
public sealed class ThemeReturnToTests
{
    [Theory]
    [InlineData("GET", "/accounts", "?q=a", "/accounts?q=a")]
    [InlineData("HEAD", "/", "", "/")]
    [InlineData("POST", "/opportunities/12/stage", "", "/opportunities/12")]
    [InlineData("POST", "/accounts/5/edit", "?x=1", "/accounts/5")]
    [InlineData("POST", "/opportunities/12/won", "", "/opportunities/12")]
    [InlineData("POST", "/opportunities", "", "/")]
    [InlineData("POST", "/accounts/abc/edit", "", "/")]
    [InlineData("POST", "/settings/stages", "", "/")]
    public void Non_get_requests_go_to_the_record_page_or_the_dashboard(string method, string path, string query, string expected)
    {
        Assert.Equal(expected, Crm.Web.Pages.Ui.ThemeReturnTo(method, path, query));
    }
}
