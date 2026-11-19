using System.Security.Cryptography;
using System.Text;
using Crm.Web.Data;
using Crm.Web.Services;

namespace Crm.Web.Infrastructure;

public static class BrandingEndpoints
{
    /// <summary>
    /// The Admin accent applies to the Light theme only (Dark keeps white, because no accent that passes in Light can pass on the dark gray).
    /// An unset (legacy default) or too-light accent emits no override, so the DAFA blue from app.css shows.
    /// </summary>
    public static string ThemeCss(string storedAccent)
    {
        var accent = Color.EffectiveAccent(storedAccent);
        var preview = accent ?? Color.DefaultAccent;
        var css = $":root {{ --accent-preview: {preview}; }}\n";
        if (accent is null)
        {
            return css;
        }
        var vars = $"--action: {accent}; --action-hover: {Color.Darken(accent)};";
        return css + $"@media (prefers-color-scheme: light) {{ :root:not([data-theme=\"dark\"]) {{ {vars} }} }}\n:root[data-theme=\"light\"] {{ {vars} }}\n";
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        // Accent colour as a stylesheet, because the CSP forbids inline styles. Validated on write and re-checked here.
        app.MapGet("/css/theme.css", async (HttpContext ctx, BrandingCache branding) =>
        {
            var b = await branding.GetAsync(ctx.RequestAborted);
            var css = ThemeCss(b.AccentColor);
            var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(css)))[..16] + "\"";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.ETag = etag;
            if (ctx.Request.Headers.IfNoneMatch == etag)
            {
                return Results.StatusCode(304);
            }
            return Results.Text(css, "text/css; charset=utf-8");
        }).AllowAnonymous();

        app.MapGet("/branding/logo", async (HttpContext ctx, BrandingCache branding) =>
        {
            var b = await branding.GetAsync(ctx.RequestAborted);
            if (b.LogoSha is null)
            {
                return Results.Text("Not found", "text/plain", statusCode: 404);
            }
            var etag = "\"" + b.LogoSha + "\"";
            var h = ctx.Response.Headers;
            h.ETag = etag;
            h.CacheControl = "no-cache";
            h.XContentTypeOptions = "nosniff";
            h.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            if (ctx.Request.Headers.IfNoneMatch == etag)
            {
                return Results.StatusCode(304);
            }
            var logo = await branding.GetLogoAsync(ctx.RequestAborted);
            return logo is null ? Results.Text("Not found", "text/plain", statusCode: 404) : Results.Bytes(logo.Content, logo.ContentType);
        }).AllowAnonymous();
    }
}
