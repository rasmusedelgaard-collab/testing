using Crm.Web.Config;

namespace Crm.Web.Infrastructure;

/// <summary>Replaces helmet (dotnet.md 13.1). The logo endpoint overrides the CSP header itself.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, CrmConfig config)
{
    private readonly string _csp = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self' https://login.microsoftonline.com"
        + (config.SecureCookies ? "; upgrade-insecure-requests" : "");

    public Task InvokeAsync(HttpContext context)
    {
        var h = context.Response.Headers;
        h["Content-Security-Policy"] = _csp;
        h["Referrer-Policy"] = "same-origin";
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "SAMEORIGIN";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h["Origin-Agent-Cluster"] = "?1";
        h["X-DNS-Prefetch-Control"] = "off";
        h["X-Permitted-Cross-Domain-Policies"] = "none";
        h["X-XSS-Protection"] = "0";
        return next(context);
    }
}
