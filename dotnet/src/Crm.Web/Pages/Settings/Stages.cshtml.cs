using System.Globalization;
using System.Text.RegularExpressions;
using Crm.Web.Data;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Settings;

public sealed record StageRowVm(string Id, string Name, string Prob, string Type, int Used);

/// <summary>
/// Pipeline stages (GET/POST /settings/stages). The editor is one form: Add, Move up/down and Remove only re-render (nothing is saved
/// until Save stages); Save validates and writes everything in one transaction.
/// </summary>
public sealed partial class StagesModel(IRlsDb rls, IRlsContextAccessor identity, BrandingCache branding) : SettingsPageModel(rls, identity, branding)
{
    private IReadOnlyList<string> _pageSummary = [];

    public IReadOnlyList<StageRowVm> Rows { get; private set; } = [];

    /// <summary>Hash of the stage list as loaded; it travels with the form and a stale save is refused (see AdminRepo.StageVersion).</summary>
    public string Version { get; private set; } = "";

    public string Note { get; private set; } = "";

    /// <summary>True when the save was refused because the list changed meanwhile: a focused page-level alert with a link to reload, not part of the field-error summary.</summary>
    public bool Stale { get; private set; }

    public string Focus { get; private set; } = "";

    public Dictionary<string, string> Links { get; } = [];

    /// <summary>Row problems first (each links to its field, prefixed with the row number), then page-level ones.</summary>
    public override IReadOnlyList<(string Field, string Message)> Summary =>
        Errors.Select(e => (e.Key, $"Row {int.Parse(NonDigits().Replace(e.Key, ""), CultureInfo.InvariantCulture) + 1}: {e.Value}")).Concat(_pageSummary.Select(m => ("", m))).ToList();

    [GeneratedRegex(@"[^0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NonDigits();

    [GeneratedRegex(@"^(up|down|remove):([0-9]{1,2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex RowAction();

    private void Show(List<StageRowVm> rows, IReadOnlyList<StageUsageRow> usage, Dictionary<string, string>? errors = null, IReadOnlyList<string>? summary = null, string note = "", string focus = "", string? version = null)
    {
        Version = version ?? AdminRepo.StageVersion(usage);
        Setup("stages", "Pipeline stages");
        var used = usage.ToDictionary(u => u.Id.ToString(CultureInfo.InvariantCulture), u => u.Used);
        // The type of a stage in use is locked, so it always shows (and posts) the stored type, never an attempted change.
        var storedType = usage.ToDictionary(u => u.Id.ToString(CultureInfo.InvariantCulture), u => u.Type);
        Rows = rows.Select(r => r with { Used = used.GetValueOrDefault(r.Id), Type = used.GetValueOrDefault(r.Id) > 0 && storedType.TryGetValue(r.Id, out var t) ? t : r.Type }).ToList();
        Errors = errors ?? [];
        _pageSummary = summary ?? [];
        Note = note;
        Focus = focus;
    }

    private static string S(string? v) => v ?? "";

    public async Task<IActionResult> OnGetAsync()
    {
        var usage = await RunAsync(async (admin, _) => await admin.StageUsageAsync());
        Show(usage.Select(u => new StageRowVm(u.Id.ToString(CultureInfo.InvariantCulture), u.Name, u.Probability.ToString(CultureInfo.InvariantCulture), u.Type, 0)).ToList(), usage);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var form = Request.Form;
        string F(string k) => form.TryGetValue(k, out var v) ? v.ToString() : "";
        var action = F("action");
        var posted = F("ver");
        var n = int.TryParse(F("n"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? Math.Min(parsed, 50) : 0;
        var rows = Enumerable.Range(0, n).Select(i => new StageRowVm(F($"id_{i}"), F($"name_{i}"), F($"prob_{i}"), F($"type_{i}"), 0)).ToList();
        var m = RowAction().Match(action);
        if (action == "add" || m.Success)
        {
            return await EditInPlaceAsync(action, m, rows, posted);
        }
        var (valid, errors, summary) = SettingsValidators.ValidateStages(rows.Select(r => new SettingsValidators.StageForm(r.Id, r.Name, r.Prob, r.Type)).ToList());
        var meta = MetaOf();
        var (usage, saved) = await RunAsync(async (admin, _) =>
        {
            var u = await admin.StageUsageAsync();
            if (errors.Count > 0 || summary.Count > 0) { return (u, (AdminRepo.StageSaveResult?)null); }
            return (u, await admin.SaveStagesAsync(valid, posted, meta));
        });
        if (errors.Count > 0 || summary.Count > 0)
        {
            Show(rows, usage, errors, summary, version: posted);
            return Invalid();
        }
        if (saved is { Ok: false })
        {
            Show(rows, usage, saved.RowErrors?.ToDictionary(e => e.Key, e => e.Value), saved.Error is null ? null : [saved.Error], version: posted);
            Stale = saved.Stale;
            return Invalid();
        }
        Flash("Settings saved.");
        return SeeOther("/settings/stages");
    }

    private async Task<IActionResult> EditInPlaceAsync(string action, Match m, List<StageRowVm> rows, string posted)
    {
        var usage = await RunAsync(async (admin, _) => await admin.StageUsageAsync());
        var focus = "";
        if (action == "add")
        {
            rows.Add(new StageRowVm("", "", "10", "open", 0));
            focus = $"f-name{rows.Count - 1}";
        }
        else
        {
            var verb = m.Groups[1].Value;
            var i = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (i < rows.Count && verb == "remove")
            {
                var row = rows[i];
                // Refuse right here (the row stays) instead of failing at Save and losing the row and other unsaved edits.
                var inUse = usage.FirstOrDefault(u => u.Id.ToString(CultureInfo.InvariantCulture) == row.Id)?.Used ?? 0;
                var sameType = rows.Count(r => r.Type == row.Type);
                if (inUse > 0)
                {
                    var name = row.Name.Length > 0 ? row.Name : "This stage";
                    Links[$"name{i}"] = $"/opportunities?stage={Uri.EscapeDataString(row.Id)}&status=all";
                    Show(rows, usage, new Dictionary<string, string> { [$"name{i}"] = AdminRepo.StageInUse("remove", name, inUse) }, focus: $"f-name{i}", version: posted);
                    return Invalid();
                }
                if (row.Type is "won" or "lost" && sameType <= 1)
                {
                    var label = row.Type == "won" ? "Won" : "Lost";
                    Show(rows, usage, new Dictionary<string, string> { [$"name{i}"] = $"Can't remove the only {label} stage. Make another stage {label} first." }, focus: $"f-name{i}", version: posted);
                    return Invalid();
                }
                rows.RemoveAt(i);
                focus = rows.Count > 0 ? $"f-name{Math.Min(i, rows.Count - 1)}" : "";
            }
            else if (i < rows.Count && verb != "remove")
            {
                var j = verb == "up" ? i - 1 : i + 1;
                if (j >= 0 && j < rows.Count)
                {
                    (rows[i], rows[j]) = (rows[j], rows[i]);
                    // Keep the focus on the same button of the moved row, or the opposite one when it reached an end.
                    var atEnd = verb == "up" ? j == 0 : j == rows.Count - 1;
                    focus = $"{(atEnd ? (verb == "up" ? "down" : "up") : verb)}-{j}";
                }
            }
        }
        Show(rows, usage, note: "Changes are not saved yet. Choose Save stages to keep them.", focus: focus, version: posted);
        return Page();
    }
}
