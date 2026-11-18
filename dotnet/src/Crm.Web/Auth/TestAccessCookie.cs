using System.Globalization;
using Crm.Web.Config;
using Microsoft.AspNetCore.DataProtection;

namespace Crm.Web.Auth;

/// <summary>
/// "Code verified" window (10 minutes) in a separate cookie, value protected with a time-limited data protector.
/// The issue time is also embedded and checked against TimeProvider, so the window is testable with a fake clock.
/// </summary>
public sealed class TestAccessCookie(IDataProtectionProvider dp, CrmConfig config, TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly ITimeLimitedDataProtector _protector = dp.CreateProtector("crm.test-access.v1").ToTimeLimitedDataProtector();
    private string Name => config.SecureCookies ? "__Host-crm.tac" : "crm.tac";

    public void Issue(HttpContext ctx)
    {
        var stamp = time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        ctx.Response.Cookies.Append(Name, _protector.Protect(stamp, Window), new CookieOptions
        {
            HttpOnly = true,
            Secure = config.SecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });
    }

    public bool IsVerified(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(Name, out var value) || string.IsNullOrEmpty(value))
        {
            return false;
        }
        try
        {
            var stamp = _protector.Unprotect(value);
            return long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
                && time.GetUtcNow().ToUnixTimeMilliseconds() - ms < Window.TotalMilliseconds;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    public void Delete(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(Name, new CookieOptions { Secure = config.SecureCookies, SameSite = SameSiteMode.Strict, Path = "/", HttpOnly = true });
}
