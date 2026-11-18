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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _used = new(StringComparer.Ordinal);
    private string Name => config.SecureCookies ? "__Host-crm.tac" : "crm.tac";

    public void Issue(HttpContext ctx)
    {
        // The random nonce makes every issued cookie unique, so a captured copy can be refused after its first use (TryConsume).
        var nonce = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var stamp = $"{time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}.{nonce}";
        ctx.Response.Cookies.Append(Name, _protector.Protect(stamp, Window), new CookieOptions
        {
            HttpOnly = true,
            Secure = config.SecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });
    }

    public bool IsVerified(HttpContext ctx) => Read(ctx) is not null;

    /// <summary>
    /// Single use: true once per issued cookie. Used nonces are remembered until their window has passed (in memory: this mode runs
    /// on one TEST instance; a restart also invalidates the cookies' 10-minute window anyway).
    /// </summary>
    public bool TryConsume(HttpContext ctx)
    {
        if (Read(ctx) is not { } c)
        {
            return false;
        }
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var (k, issued) in _used)
        {
            if (now - issued >= Window.TotalMilliseconds) { _used.TryRemove(k, out _); }
        }
        return _used.TryAdd(c.Nonce, c.IssuedMs);
    }

    private (long IssuedMs, string Nonce)? Read(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(Name, out var value) || string.IsNullOrEmpty(value))
        {
            return null;
        }
        try
        {
            var parts = _protector.Unprotect(value).Split('.');
            return parts.Length == 2 && parts[1].Length > 0 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
                && time.GetUtcNow().ToUnixTimeMilliseconds() - ms < Window.TotalMilliseconds ? (ms, parts[1]) : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public void Delete(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(Name, new CookieOptions { Secure = config.SecureCookies, SameSite = SameSiteMode.Strict, Path = "/", HttpOnly = true });
}
