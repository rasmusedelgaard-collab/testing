using Crm.Web.Data;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Settings;

public sealed class VisibilityModel(IRlsDb rls, IRlsContextAccessor identity, BrandingCache branding) : SettingsPageModel(rls, identity, branding)
{
    public string Mode { get; private set; } = "private";

    private async Task<string> CurrentAsync() => (await RunAsync(async (_, repo) => (await repo.LookupsAsync()).Settings.VisibilityMode));

    public async Task<IActionResult> OnGetAsync()
    {
        Setup("visibility", "Visibility");
        Mode = await CurrentAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? mode, string? confirm)
    {
        Setup("visibility", "Visibility");
        var meta = MetaOf();
        var result = await RunAsync<(string Kind, string Current)>(async (admin, repo) =>
        {
            var current = (await repo.LookupsAsync()).Settings.VisibilityMode;
            if (mode is not ("private" or "team")) { return ("invalid", current); }
            if (mode != current && confirm != "1") { return ("confirm", current); }
            await admin.SaveSettingsAsync(new Dictionary<string, string> { ["visibility.mode"] = mode }, meta);
            return ("ok", current);
        });
        Mode = result.Current;
        switch (result.Kind)
        {
            case "invalid":
                Errors = new Dictionary<string, string> { ["mode"] = "Choose who can see records." };
                return Invalid();
            case "confirm":
                return ShowConfirm("Change visibility", "This changes what everyone can see immediately. Continue?", "/settings/visibility", [("mode", mode!), ("confirm", "1")], "Change visibility", false, "/settings/visibility");
            default:
                Flash("Settings saved.");
                return SeeOther("/settings/visibility");
        }
    }
}
