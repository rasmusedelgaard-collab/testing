using Crm.Web.Config;
using Crm.Web.Data;
using Crm.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Identity.Web;

namespace Crm.Web.Auth;

public static class AuthSetup
{
    public const string Cookies = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string AdminPolicy = "Admin";

    public static string SessionCookieName(CrmConfig c) => c.SecureCookies ? "__Host-crm.session" : "crm.session";

    public static void AddCrmAuthentication(this IServiceCollection services, CrmConfig config)
    {
        services.AddSingleton(new SessionTimeouts(config.SessionIdleMinutes, config.SessionAbsoluteHours));
        services.AddSingleton<SqlTicketStore>();
        services.AddSingleton<SessionValidator>();
        services.AddSingleton<EntraSignIn>();
        services.AddSingleton<UserRepo>();

        void ConfigureCookie(CookieAuthenticationOptions o)
        {
            o.Cookie.Name = SessionCookieName(config);
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/";
            o.Cookie.SecurePolicy = config.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.Cookie.IsEssential = true;
            o.ExpireTimeSpan = TimeSpan.FromHours(config.SessionAbsoluteHours);
            o.SlidingExpiration = false; // the idle timeout is enforced in the ticket store
            o.Events.OnRedirectToLogin = ctx =>
            {
                var target = HttpMethods.IsGet(ctx.Request.Method)
                    ? "/signin?returnTo=" + Uri.EscapeDataString(ReturnTo.Safe(ctx.Request.GetEncodedPathAndQuery()))
                    : "/signin";
                if (ctx.HttpContext.Items[SessionValidator.ReasonItem] is string reason)
                {
                    target += (target.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "reason=" + reason;
                }
                ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
                ctx.Response.Headers.Location = target;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
            o.Events.OnValidatePrincipal = ctx => ctx.HttpContext.RequestServices.GetRequiredService<SessionValidator>().ValidateAsync(ctx);
        }

        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = Cookies;
            o.DefaultChallengeScheme = Cookies; // unauthenticated -> /signin; /auth/login challenges OIDC explicitly
        });

        if (config.IsTestAccess)
        {
            auth.AddCookie(Cookies, ConfigureCookie);
        }
        else
        {
            auth.AddMicrosoftIdentityWebApp(
                o =>
                {
                    o.Instance = "https://login.microsoftonline.com/";
                    o.TenantId = config.Entra.TenantId;
                    o.ClientId = config.Entra.ClientId;
                    o.ClientSecret = config.Entra.ClientSecret;
                    o.CallbackPath = "/auth/redirect";
                    o.ResponseType = "code";
                    o.UsePkce = true;
                    o.ResponseMode = "query";
                    o.SaveTokens = false;
                    o.MapInboundClaims = false;
                    o.GetClaimsFromUserInfoEndpoint = false;
                    o.Scope.Clear();
                    o.Scope.Add("openid");
                    o.Scope.Add("profile");
                    o.Scope.Add("email");
                    o.SignInScheme = Cookies;
                    o.CorrelationCookie.SameSite = SameSiteMode.Lax;
                    o.CorrelationCookie.HttpOnly = true;
                    o.CorrelationCookie.SecurePolicy = config.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                    o.NonceCookie.SameSite = SameSiteMode.Lax;
                    o.NonceCookie.HttpOnly = true;
                    o.NonceCookie.SecurePolicy = config.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                    o.Events ??= new OpenIdConnectEvents();
                    o.Events.OnRedirectToIdentityProvider = ctx =>
                    {
                        // Never derive redirect_uri from the request host behind proxies.
                        ctx.ProtocolMessage.RedirectUri = config.RedirectUri;
                        // The callback is a top-level GET, so SameSite=Lax cookies are sent (ADR-0003). Say it explicitly.
                        ctx.ProtocolMessage.ResponseMode = "query";
                        return Task.CompletedTask;
                    };
                    o.Events.OnTokenValidated = OnTokenValidated;
                    o.Events.OnRemoteFailure = OnRemoteFailure;
                },
                ConfigureCookie,
                OpenIdConnectDefaults.AuthenticationScheme,
                Cookies);
            Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = false;
        }

        services.AddOptions<CookieAuthenticationOptions>(Cookies)
            .Configure<SqlTicketStore>((o, store) => o.SessionStore = store);

        services.AddAuthorization(o =>
        {
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            o.AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().RequireRole(CrmRole.Admin.ToString()));
        });
    }

    private static async Task OnTokenValidated(TokenValidatedContext ctx)
    {
        var sp = ctx.HttpContext.RequestServices;
        var config = sp.GetRequiredService<CrmConfig>();
        var result = await sp.GetRequiredService<EntraSignIn>().ProcessAsync(ctx.Principal!.Claims.ToList(), config.Entra.TenantId, ctx.HttpContext.RequestAborted);
        switch (result.Outcome)
        {
            case SignInOutcome.Ok:
                ctx.Principal = result.Principal;
                break;
            case SignInOutcome.TenantMismatch:
                ctx.Fail("tenant mismatch");
                break;
            default:
                ctx.HandleResponse();
                ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
                ctx.Response.Headers.Location = "/no-access";
                break;
        }
    }

    private static Task OnRemoteFailure(RemoteFailureContext ctx)
    {
        var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Crm.Web.Auth.Entra");
        log.LogWarning("sign-in failed: {Reason}", ctx.Failure?.GetType().Name ?? "unknown");
        ctx.HandleResponse();
        // As the Node app: tenant mismatch 403, token redeem failure 502, everything else (state, nonce, idp error) 400.
        var message = ctx.Failure?.Message ?? "";
        var status = message.Contains("tenant mismatch", StringComparison.OrdinalIgnoreCase) ? 403
            : message.Contains("redeem", StringComparison.OrdinalIgnoreCase) ? 502 : 400;
        ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
        ctx.Response.Headers.Location = $"/signin-error?ref={RequestContextMiddleware.CorrelationId(ctx.HttpContext)}&code={status}";
        return Task.CompletedTask;
    }
}
