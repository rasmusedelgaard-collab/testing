using Crm.Web.Data;
using Crm.Web.Infrastructure;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Settings;

/// <summary>
/// Edit a team (GET /settings/teams/{id}/edit, POST /settings/teams/{id}), deactivate (GET/POST .../deactivate) and reactivate (POST .../reactivate).
/// The extra routes are added in Program.cs.
/// </summary>
public sealed class TeamModel(IRlsDb rls, IRlsContextAccessor identity, BrandingCache branding) : SettingsPageModel(rls, identity, branding)
{
    public TeamRow Team { get; private set; } = null!;

    public string NameValue { get; private set; } = "";

    public string GroupValue { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(int id, string? verb)
    {
        Setup("users", "Team");
        var t = await RunAsync(async (admin, _) => await admin.TeamAsync(id)) ?? throw new NotFoundException();
        Team = t;
        if (verb == "deactivate")
        {
            return ShowConfirm("Deactivate team", $"Deactivate {t.Name}? Its {t.Members} members will have no team from their next sign-in and will see only their own records. Their records keep their owners.",
                $"/settings/teams/{id}/deactivate", [], "Deactivate team", true, "/settings/users");
        }
        if (verb == "reactivate")
        {
            throw new NotFoundException();
        }
        ViewData["Title"] = $"Edit {t.Name}";
        NameValue = t.Name;
        GroupValue = t.GroupId;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, string? verb, string? name, string? groupId, string? confirm)
    {
        Setup("users", "Team");
        var meta = MetaOf();
        if (verb is "deactivate" or "reactivate")
        {
            var active = verb == "reactivate";
            var t = await RunAsync(async (admin, _) => await admin.SetTeamActiveAsync(id, active, meta)) ?? throw new NotFoundException();
            Flash($"Team {t.Name} {(active ? "reactivated" : "deactivated")}.");
            return SeeOther("/settings/users");
        }
        var (n, g, errors) = SettingsValidators.ValidateTeam(name, groupId);
        var (team, outcome, needsConfirm) = await RunAsync(async (admin, _) =>
        {
            var existing = await admin.TeamAsync(id);
            if (existing is null) { return ((TeamRow?)null, (SaveOutcomeAdmin?)null, false); }
            if (errors.Count > 0) { return (existing, null, false); }
            if (g != existing.GroupId && confirm != "1") { return (existing, null, true); }
            return (existing, await admin.SaveTeamAsync(id, n, g, meta), false);
        });
        Team = team ?? throw new NotFoundException();
        if (needsConfirm)
        {
            return ShowConfirm("Change group", $"Changing the group changes who is in {team.Name}. People are moved at their next sign-in. Continue?", $"/settings/teams/{id}",
                [("name", n), ("groupId", g), ("confirm", "1")], "Save team", false, "/settings/users");
        }
        if (outcome is { Ok: true })
        {
            Flash($"Team {n} saved.");
            return SeeOther("/settings/users");
        }
        if (outcome is not null) { errors[outcome.Field ?? "name"] = outcome.Error!; }
        Errors = errors;
        ViewData["Title"] = $"Edit {team.Name}";
        NameValue = outcome is null ? name ?? "" : n;
        GroupValue = outcome is null ? groupId ?? "" : g;
        return Invalid();
    }
}
