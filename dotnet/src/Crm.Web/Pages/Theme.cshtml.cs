using Crm.Web.Auth;
using Crm.Web.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages;

/// <summary>
/// POST /theme (UI redesign B-1): stores the Light/Dark choice in a plain preference cookie and goes back. "system" removes the cookie.
/// The cookie holds no personal data and does not depend on the session, so the choice survives sign-out. Antiforgery is validated by Razor Pages.
/// </summary>
[AllowAnonymous]
public sealed class ThemeModel(CrmConfig config, TimeProvider time) : PageModel
{
    public const string CookieName = "theme";

    public IActionResult OnGet() => NotFound();

    public IActionResult OnPost()
    {
        var choice = Request.Form["theme"].FirstOrDefault();
        if (choice is "light" or "dark")
        {
            Response.Cookies.Append(CookieName, choice, new CookieOptions
            {
                Path = "/",
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = config.SecureCookies,
                IsEssential = true,
                Expires = time.GetUtcNow().AddYears(1),
            });
        }
        else if (choice == "system")
        {
            Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", Secure = config.SecureCookies, SameSite = SameSiteMode.Lax });
        }
        Response.StatusCode = StatusCodes.Status303SeeOther;
        Response.Headers.Location = ReturnTo.Safe(Request.Form["returnTo"].FirstOrDefault(), "/");
        return new EmptyResult();
    }
}
