using Crm.Web.Auth;
using Crm.Web.Config;
using Crm.Web.Data;
using Crm.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages.Auth;

[AllowAnonymous]
public sealed class TestLoginUserModel(CrmConfig config, TestAccessCookie cookie, TestUserRepo users, TimeProvider time, ILogger<TestLoginUserModel> log) : PageModel
{
    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnPostAsync(string? oid, string? returnTo, CancellationToken ct)
    {
        if (!config.IsTestAccess)
        {
            return NotFound();
        }
        if (!cookie.IsVerified(HttpContext))
        {
            return RedirectSeeOther("/auth/test-login");
        }
        var row = await users.FindAsync((oid ?? "").ToLowerInvariant(), ct);
        if (row is null)
        {
            throw new ForbiddenException("That test user is not available.");
        }
        // Single use (security review M3): only a successful sign-in spends the cookie, so a replayed copy goes back to the code page.
        if (!cookie.TryConsume(HttpContext))
        {
            return RedirectSeeOther("/auth/test-login");
        }
        var user = new SessionUser(row.UserId, row.Oid, row.Name, row.Email, row.Role, row.TeamId, time.GetUtcNow().ToUnixTimeMilliseconds());
        // SignInAsync creates a NEW ticket-store row (fixation-safe), identical to the Entra path from here on.
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CrmPrincipal.Create(user), new AuthenticationProperties { IsPersistent = false });
        cookie.Delete(HttpContext);
        log.LogInformation("test login: oid {Oid} role {Role}", row.Oid, row.Role);
        return RedirectSeeOther(Crm.Web.Auth.ReturnTo.Safe(returnTo));
    }

    private IActionResult RedirectSeeOther(string url)
    {
        Response.StatusCode = StatusCodes.Status303SeeOther;
        Response.Headers.Location = url;
        return new EmptyResult();
    }
}
