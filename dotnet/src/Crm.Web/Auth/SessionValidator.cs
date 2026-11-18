using System.Globalization;
using Crm.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;

namespace Crm.Web.Auth;

/// <summary>Port of requireAuth: absolute timeout and the 5-minute IsActive re-check, in OnValidatePrincipal.</summary>
public sealed class SessionValidator(UserRepo users, TimeProvider time, SessionTimeouts options, ILogger<SessionValidator> log)
{
    public const string ReasonItem = "crm.signout.reason";
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(5);

    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var user = CrmPrincipal.ToSessionUser(context.Principal!);
        if (user is null)
        {
            await Reject(context, null).ConfigureAwait(false);
            return;
        }
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (now - user.AuthAtMs > options.AbsoluteHours * 3_600_000L)
        {
            await Reject(context, "expired").ConfigureAwait(false);
            return;
        }
        var checkedAt = context.Properties.Items.TryGetValue(CrmClaims.ActiveCheckedAtItem, out var s) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : user.AuthAtMs;
        if (now - checkedAt > RecheckInterval.TotalMilliseconds)
        {
            bool active;
            try
            {
                active = await users.IsActiveAsync(user.UserId, context.HttpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException && Transient.IsTransient(e))
            {
                throw new Infrastructure.DbWakingException(e);
            }
            if (!active)
            {
                log.LogInformation("session rejected: user {UserId} is no longer active", user.UserId);
                await Reject(context, "disabled").ConfigureAwait(false);
                return;
            }
            context.Properties.Items[CrmClaims.ActiveCheckedAtItem] = now.ToString(CultureInfo.InvariantCulture);
            context.ShouldRenew = true;
        }
    }

    private static async Task Reject(CookieValidatePrincipalContext context, string? reason)
    {
        if (reason is not null)
        {
            context.HttpContext.Items[ReasonItem] = reason;
        }
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
    }
}
