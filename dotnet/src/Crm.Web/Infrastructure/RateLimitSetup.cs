using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Crm.Web.Infrastructure;

/// <summary>One GlobalLimiter keyed by (policy, client IP), policy chosen by path (dotnet.md 13.2). In memory, per instance.</summary>
public static class RateLimitSetup
{
    public static void Configure(RateLimiterOptions o)
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        {
            var path = ctx.Request.Path;
            var (policy, limit) = path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase) ? ("auth", 120)
                : path.Equals("/readyz", StringComparison.OrdinalIgnoreCase) ? ("readyz", 30)
                : path.Equals("/branding/logo", StringComparison.OrdinalIgnoreCase) ? ("logo", 120)
                : ("", 0);
            if (limit == 0)
            {
                return RateLimitPartition.GetNoLimiter("none");
            }
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "conn:" + ctx.Connection.Id;
            return RateLimitPartition.GetFixedWindowLimiter(policy + "|" + ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromSeconds(60),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        });
        o.OnRejected = async (context, ct) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
            {
                context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.HttpContext.Response.ContentType = "text/plain";
            await context.HttpContext.Response.WriteAsync("Too many requests", ct).ConfigureAwait(false);
        };
    }
}
