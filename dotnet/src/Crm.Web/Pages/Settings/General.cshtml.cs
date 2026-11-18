using Crm.Web.Data;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Settings;

/// <summary>General: CRM name, accent colour (GET/POST /settings/general) and logo (POST /settings/general/logo, same page via a second route).</summary>
[RequestFormLimits(MultipartBodyLengthLimit = 600_000)]
[RequestSizeLimit(600_000)]
public sealed class GeneralModel(IRlsDb rls, IRlsContextAccessor identity, BrandingCache branding) : SettingsPageModel(rls, identity, branding)
{
    public string AppName { get; private set; } = "CRM";

    public string AccentColor { get; private set; } = "#1D4ED8";

    public string? LogoSha { get; private set; }

    private async Task LoadAsync(bool keepValues, string? appName = null, string? accent = null)
    {
        var (map, sha) = await RunAsync(async (admin, _) => (await admin.SettingsMapAsync(), await admin.LogoShaAsync()));
        LogoSha = sha;
        AppName = keepValues ? appName ?? "" : map.GetValueOrDefault("app.name") ?? "CRM";
        AccentColor = keepValues ? accent ?? "" : map.GetValueOrDefault("branding.accentColor") ?? "#1D4ED8";
        Setup("general", "General settings");
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync(false);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? appName, string? accentColor)
    {
        var (name, accent, errors) = SettingsValidators.ValidateGeneral(appName, accentColor);
        if (errors.Count > 0)
        {
            await LoadAsync(true, appName ?? "", accentColor ?? "");
            Errors = errors;
            return Invalid();
        }
        var meta = MetaOf();
        await RunAsync((admin, _) => Task.Run(async () =>
        {
            await admin.SaveSettingsAsync(new Dictionary<string, string> { ["app.name"] = name, ["branding.accentColor"] = accent }, meta);
            return 0;
        }));
        Branding.Invalidate();
        Flash("Settings saved.");
        return SeeOther("/settings/general");
    }

    /// <summary>Logo upload: memory only, strict limits, content sniffed (never the filename or declared type).</summary>
    public async Task<IActionResult> OnPostLogoAsync(IFormFile? logo)
    {
        string? error = null;
        byte[]? bytes = null;
        if (logo is null)
        {
            error = "Choose a logo file first.";
        }
        else if (logo.Length > SettingsValidators.MaxLogoBytes)
        {
            error = "Logo must be a PNG, 500 KB or smaller.";
        }
        else
        {
            await using var ms = new MemoryStream();
            await logo.CopyToAsync(ms, HttpContext.RequestAborted);
            bytes = ms.ToArray();
            var check = SettingsValidators.CheckLogo(bytes);
            if (!check.Ok)
            {
                error = check.Message;
            }
            else
            {
                var meta = MetaOf();
                var sha = check.Sha256!;
                var hex = Convert.ToHexStringLower(sha);
                await RunAsync(async (admin, _) =>
                {
                    await admin.SaveLogoAsync(bytes, sha, hex, meta);
                    return 0;
                });
                Branding.Invalidate();
                Flash("Logo saved.");
                return SeeOther("/settings/general");
            }
        }
        await LoadAsync(false);
        Errors = new Dictionary<string, string> { ["logo"] = error! };
        return Invalid();
    }
}
