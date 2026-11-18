using System.Text.Encodings.Web;
using System.Text.Unicode;
using Crm.Web.Auth;
using Crm.Web.Config;
using Crm.Web.Data;
using Crm.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.WebEncoders;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// Entry point. CLI commands (migrate | seed-test | run-sql <file>) load only the database configuration and never build a web host.
if (args.Length > 0 && args[0] is "migrate" or "seed-test" or "run-sql")
{
    return await CliRunner.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

CrmConfig config;
try
{
    config = CrmConfigLoader.Load(builder.Configuration);
}
catch (ConfigException e)
{
    // Keys only, never values. Under IIS this shows as HTTP 500.30.
    await Console.Error.WriteLineAsync(e.Message);
    return 1;
}

ConfigureLogging(builder, config);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(TimeProvider.System);
builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;
    o.Limits.MaxRequestBodySize = 1_000_000;
});

// Data layer
builder.Services.AddSingleton(new SqlConnectionFactory(config.Db));
builder.Services.AddSingleton<DbRetry>();
builder.Services.AddSingleton<SystemDb>();
builder.Services.AddSingleton<BrandingCache>();
builder.Services.AddSingleton<ReadinessProbe>();
builder.Services.AddSingleton<TestUserRepo>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRlsContextAccessor, HttpRlsContextAccessor>();
builder.Services.AddScoped<RlsDb>();
builder.Services.AddScoped<IRlsDb>(sp => sp.GetRequiredService<RlsDb>());

// Auth
builder.Services.AddCrmAuthentication(config);
if (config.IsTestAccess)
{
    builder.Services.AddSingleton(sp => new TestAccessGate(config.TestAccessCode!, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<TestAccessGate>>()));
    builder.Services.AddSingleton<TestAccessCookie>();
}
else
{
    // Pages inject these in every mode; entra mode never serves the test-login pages (they return 404).
    builder.Services.AddSingleton(sp => new TestAccessGate("", sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<TestAccessGate>>()));
    builder.Services.AddSingleton<TestAccessCookie>();
}

// Data Protection keys are operational state (sessions, antiforgery, TempData, test-access cookie).
var dp = builder.Services.AddDataProtection().SetApplicationName("crm");
var keysPath = config.DataProtectionKeysPath
    ?? (builder.Configuration["WEBSITE_INSTANCE_ID"] is not null ? "/home/ASP.NET/DataProtection-Keys"
        : builder.Configuration["APP_POOL_ID"] is not null ? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtection-Keys")
        : null);
if (keysPath is not null)
{
    dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = config.SecureCookies ? "__Host-crm.af" : "crm.af";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = config.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.FormFieldName = "_csrf";
    o.HeaderName = "X-CSRF-TOKEN";
});
builder.Services.Configure<CookieTempDataProviderOptions>(o =>
{
    o.Cookie.Name = config.SecureCookies ? "__Host-crm.tmp" : "crm.tmp";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = config.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.Cookie.IsEssential = true;
});
builder.Services.Configure<WebEncoderOptions>(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.Services.AddRateLimiter(RateLimitSetup.Configure);
builder.Services.Configure<RouteOptions>(o =>
{
    o.ConstraintMap["crmentity"] = typeof(CrmEntityConstraint);
    o.ConstraintMap["crmid"] = typeof(CrmIdConstraint);
});
builder.Services.AddHttpsRedirection(o =>
{
    o.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
    o.HttpsPort = 443;
});
builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = true;
});
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Settings", AuthSetup.AdminPolicy);
    // Extra routes for the generic entity pages (a Razor Page has one @page template).
    o.Conventions.AddPageRoute("/Entity/Form", "{entity:crmentity}/{id:crmid}/edit");
    o.Conventions.AddPageRoute("/Entity/Detail", "opportunities/{id:crmid}/{outcome:regex(^(won|lost)$)}");
    o.Conventions.AddPageRoute("/Entity/Detail", "activities/{id:crmid}/{verb:regex(^(done|undone)$)}");
    o.Conventions.AddPageRoute("/Settings/Users", "settings/teams");
    o.Conventions.AddPageRoute("/Settings/Fx", "settings/fx/{id:crmid}");
    o.Conventions.AddPageRoute("/Settings/Fx", "settings/fx/{id:crmid}/{verb:regex(^(edit|delete)$)}");
    o.Conventions.AddPageRoute("/Settings/Team", "settings/teams/{id:crmid}");
    o.Conventions.AddPageRoute("/Settings/Team", "settings/teams/{id:crmid}/{verb:regex(^(deactivate|reactivate)$)}");
}).AddMvcOptions(o => o.Filters.Add<AntiforgeryFailureFilter>());

var app = builder.Build();

foreach (var notice in config.Notices)
{
    app.Logger.LogInformation("{Notice}", notice);
}
if (config.IsTestAccess)
{
    app.Logger.LogWarning("AUTH_MODE=test-access-code is active: shared-code sign-in for TEST data only. Never use on a live host.");
}

var probe = app.Services.GetRequiredService<ReadinessProbe>();
var zoneOk = ReadinessProbe.TimeZoneAvailable("Europe/Copenhagen");
if (!zoneOk)
{
    app.Logger.LogError("Time zone lookup failed (ICU missing?): /readyz will report fail");
}
probe.StartupChecksPassed = zoneOk;

// Middleware order: dotnet.md section 4.
if (config.TrustProxy > 0)
{
    var fh = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = config.TrustProxy,
    };
    fh.KnownIPNetworks.Clear();
    fh.KnownProxies.Clear();
    app.UseForwardedHeaders(fh);
}
app.UseMiddleware<RequestContextMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
}
app.UseStatusCodePagesWithReExecute("/error/{0}");
if (config.SecureCookies)
{
    // Secure-only cookies cannot work over http (antiforgery would throw on any form page). Health probes stay reachable over http.
    app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/healthz") && !ctx.Request.Path.StartsWithSegments("/readyz"), b => b.UseHttpsRedirection());
}
if (config.SecureCookies && !app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=3600",
});
app.Use(async (ctx, next) =>
{
    // 1 MB globally (also under IIS in-process); urlencoded forms 100 KB like express.urlencoded({ limit: '100kb' }).
    if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f)
    {
        f.MaxRequestBodySize = ctx.Request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true ? 100 * 1024 : 1_000_000;
    }
    await next(ctx);
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<NoStoreForAuthenticatedMiddleware>();
// Opportunistic session cleanup after a response (never on a timer, so a serverless database can auto-pause).
app.Use(async (ctx, next) =>
{
    ctx.Response.OnCompleted(() =>
    {
        ctx.RequestServices.GetRequiredService<SqlTicketStore>().MaybeCleanup();
        return Task.CompletedTask;
    });
    await next(ctx);
});
app.MapGet("/healthz", () => Results.Text("ok", "text/plain")).AllowAnonymous();
app.MapGet("/readyz", async (HttpContext ctx, ReadinessProbe p) =>
{
    var ok = await p.CheckAsync(ctx.RequestAborted);
    return Results.Text(ok ? "ok" : "fail", "text/plain", statusCode: ok ? 200 : 503);
}).AllowAnonymous();
BrandingEndpoints.Map(app);
app.MapRazorPages();


await app.RunAsync();
return 0;

static void ConfigureLogging(WebApplicationBuilder builder, CrmConfig config)
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole();
    var level = config.LogLevel switch
    {
        "fatal" => LogLevel.Critical,
        "error" => LogLevel.Error,
        "warn" => LogLevel.Warning,
        "debug" => LogLevel.Debug,
        "trace" => LogLevel.Trace,
        "silent" => LogLevel.None,
        _ => LogLevel.Information,
    };
    builder.Logging.SetMinimumLevel(level);
    builder.Logging.AddFilter("Microsoft.AspNetCore", level == LogLevel.Trace ? LogLevel.Debug : (level > LogLevel.Warning ? level : LogLevel.Warning));
}

public partial class Program;
