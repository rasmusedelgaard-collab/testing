using System.Diagnostics;
using Crm.Web.Auth;

namespace Crm.Web.Infrastructure;

/// <summary>New GUID per request (shown as "Reference"), one log line per request: route template, never raw path/query.</summary>
public sealed class RequestContextMiddleware(RequestDelegate next, ILogger<RequestContextMiddleware> log)
{
    public const string CorrelationKey = "CorrelationId";

    public static string CorrelationId(HttpContext c) => c.Items[CorrelationKey] as string ?? c.TraceIdentifier;

    public async Task InvokeAsync(HttpContext context)
    {
        var id = Guid.NewGuid().ToString();
        context.TraceIdentifier = id;
        context.Items[CorrelationKey] = id;
        context.Response.Headers["X-Request-Id"] = id;
        var sw = Stopwatch.StartNew();
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(none)";
            var uid = CrmPrincipal.ToSessionUser(context.User)?.UserId;
            log.LogInformation("request {CorrelationId} user {UserId} {Method} {Route} {Status} {Ms} ms", id, uid, context.Request.Method, route, context.Response.StatusCode, sw.ElapsedMilliseconds);
        }
    }
}

/// <summary>Authenticated pages carry personal data: never cache them in the browser or a proxy.</summary>
public sealed class NoStoreForAuthenticatedMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            context.Response.OnStarting(static state =>
            {
                ((HttpContext)state).Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            }, context);
        }
        return next(context);
    }
}
