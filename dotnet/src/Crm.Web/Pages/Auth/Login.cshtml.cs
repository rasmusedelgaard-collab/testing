using Crm.Web.Config;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages.Auth;

[AllowAnonymous]
public sealed class LoginModel(CrmConfig config) : PageModel
{
    public IActionResult OnGet(string? returnTo)
    {
        var safe = Crm.Web.Auth.ReturnTo.Safe(returnTo);
        if (config.IsTestAccess)
        {
            return Redirect("/auth/test-login?returnTo=" + Uri.EscapeDataString(safe));
        }
        // State, nonce and PKCE are created and validated by the OIDC handler.
        return Challenge(new AuthenticationProperties { RedirectUri = safe }, OpenIdConnectDefaults.AuthenticationScheme);
    }
}
