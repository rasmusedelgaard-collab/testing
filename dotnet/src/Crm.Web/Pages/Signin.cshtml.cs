using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages;

[AllowAnonymous]
public sealed class SigninModel : PageModel
{
    public string ReturnTo { get; private set; } = "/";
    public bool Expired { get; private set; }

    public void OnGet(string? returnTo, string? reason)
    {
        ReturnTo = Crm.Web.Auth.ReturnTo.Safe(returnTo);
        Expired = reason == "expired";
    }
}
