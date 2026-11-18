using Crm.Web.Config;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages.Auth;

[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public sealed class SignoutModel(CrmConfig config) : PageModel
{
    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnPostAsync()
    {
        // Removes the ticket-store row and deletes the cookie.
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (config.IsTestAccess)
        {
            return RedirectPreserveMethod303("/signed-out");
        }
        var logout = $"https://login.microsoftonline.com/{config.Entra.TenantId}/oauth2/v2.0/logout?post_logout_redirect_uri={Uri.EscapeDataString(config.BaseUrl + "/signed-out")}";
        return RedirectPreserveMethod303(logout);
    }

    private IActionResult RedirectPreserveMethod303(string url)
    {
        Response.StatusCode = StatusCodes.Status303SeeOther;
        Response.Headers.Location = url;
        return new EmptyResult();
    }
}
