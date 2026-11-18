using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Crm.Web.Pages.Settings;

public sealed class IndexModel : PageModel
{
    public IActionResult OnGet() => Redirect("/settings/general");
}
