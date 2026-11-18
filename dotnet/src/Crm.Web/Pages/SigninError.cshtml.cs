using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages;

[AllowAnonymous]
public sealed class SigninErrorModel : PageModel
{
    public string Reference { get; private set; } = "";

    public void OnGet(string? @ref, int? code)
    {
        // The reference is only displayed if it is a GUID (it is a correlation id, never free text).
        Reference = Guid.TryParse(@ref, out var g) ? g.ToString() : HttpContext.TraceIdentifier;
        Response.StatusCode = code == 502 ? 502 : 400;
    }
}
