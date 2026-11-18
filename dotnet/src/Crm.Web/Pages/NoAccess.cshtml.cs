using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages;

[AllowAnonymous]
public sealed class NoAccessModel : PageModel
{
    public void OnGet() => Response.StatusCode = StatusCodes.Status403Forbidden;
}
