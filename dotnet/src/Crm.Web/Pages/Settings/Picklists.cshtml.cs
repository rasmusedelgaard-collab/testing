using System.Globalization;
using System.Text.RegularExpressions;
using Crm.Web.Data;
using Crm.Web.Infrastructure;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Settings;

/// <summary>Picklists (GET/POST /settings/picklists/{code}; /settings/picklists redirects to industry). Values are never deleted, only deactivated.</summary>
public sealed partial class PicklistsModel(IRlsDb rls, IRlsContextAccessor identity, BrandingCache branding) : SettingsPageModel(rls, identity, branding)
{
    public static readonly IReadOnlyList<(string Code, string Name)> Lists = [("industry", "Industry"), ("activity_type", "Activity type"), ("lead_source", "Lead source"), ("lost_reason", "Lost reason")];

    public string Code { get; private set; } = "";

    public PicklistData List { get; private set; } = null!;

    public bool CanAdd => Code != "activity_type";

    public string LabelValue { get; private set; } = "";

    public string Focus { get; private set; } = "";

    [GeneratedRegex(@"^([1-9][0-9]{0,9})-(up|down)\z", RegexOptions.CultureInvariant)]
    private static partial Regex FocusPattern();

    [GeneratedRegex(@"^[1-9][0-9]{0,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    private static bool KnownCode(string? code) => Lists.Any(l => l.Code == code);

    private void Show(string code, PicklistData list, string label = "", string focusQuery = "")
    {
        Setup("picklists", $"Picklist: {list.Name}");
        Code = code;
        List = list;
        LabelValue = label;
        // After a move keep the focus on the same button of the moved value (or the other one when it hit an end).
        var fm = FocusPattern().Match(focusQuery);
        if (fm.Success)
        {
            var i = list.Values.ToList().FindIndex(v => v.Id.ToString(CultureInfo.InvariantCulture) == fm.Groups[1].Value);
            var up = fm.Groups[2].Value == "up";
            var atEnd = up ? i == 0 : i == list.Values.Count - 1;
            Focus = i < 0 ? "" : $"{(atEnd ? (up ? "down" : "up") : fm.Groups[2].Value)}-{fm.Groups[1].Value}";
        }
    }

    public async Task<IActionResult> OnGetAsync(string? code)
    {
        if (code is null) { return Redirect("/settings/picklists/industry"); }
        if (!KnownCode(code)) { throw new NotFoundException(); }
        var list = await RunAsync(async (admin, _) => await admin.PicklistAsync(code)) ?? throw new NotFoundException();
        Show(code, list, focusQuery: Request.Query.TryGetValue("focus", out var f) ? f.ToString() : "");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? code, string? action, string? valueId, string? label)
    {
        if (!KnownCode(code)) { throw new NotFoundException(); }
        action ??= "";
        if (action != "add" && (valueId is null || !IdPattern().IsMatch(valueId) || long.Parse(valueId, CultureInfo.InvariantCulture) > int.MaxValue)) { throw new NotFoundException(); }
        var id = action == "add" ? 0 : int.Parse(valueId!, CultureInfo.InvariantCulture);
        var text = (label ?? "").Trim();
        var meta = MetaOf();
        var (outcome, list) = await RunAsync(async (admin, _) =>
        {
            SaveOutcomeAdmin r;
            if (action is "add" or "rename")
            {
                var bad = SettingsValidators.ValidateLabel(text);
                r = bad is not null ? new SaveOutcomeAdmin(false, bad, action == "add" ? "label" : $"label{id}")
                    : action == "add" ? await admin.AddPicklistValueAsync(code!, text, meta) : await admin.RenamePicklistValueAsync(code!, id, text, meta);
            }
            else if (action is "deactivate" or "activate") { r = await admin.SetPicklistActiveAsync(code!, id, action == "activate", meta); }
            else if (action is "up" or "down") { r = await admin.MovePicklistValueAsync(code!, id, action == "up", meta); }
            else { r = new SaveOutcomeAdmin(false, "Unknown action."); }
            return (r, r.Ok ? null : await admin.PicklistAsync(code!));
        });
        if (!outcome.Ok)
        {
            Show(code!, list ?? throw new NotFoundException(), text);
            Errors = new Dictionary<string, string> { [outcome.Field ?? "label"] = outcome.Error! };
            return Invalid();
        }
        Flash("Settings saved.");
        return SeeOther($"/settings/picklists/{code}{(action is "up" or "down" ? $"?focus={id}-{action}" : "")}");
    }
}
