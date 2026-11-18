using System.Globalization;
using Crm.Web.Config;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Web.Pages.Settings;

public sealed record UserVm(UserRow User, string LastSignInText, string Badge);

/// <summary>Users and teams (GET /settings/users; POST /settings/teams adds a team through a second route).</summary>
public sealed class UsersModel(IRlsDb rls, IRlsContextAccessor identity, BrandingCache branding, CrmConfig config) : SettingsPageModel(rls, identity, branding)
{
    public string ConnectionIp { get; private set; } = "unknown";

    public int Hops => config.TrustProxy;

    public IReadOnlyList<TeamRow> Teams { get; private set; } = [];

    public IReadOnlyList<UserVm> Users { get; private set; } = [];

    public int NoTeamCount { get; private set; }

    public string FilterRole { get; private set; } = "";

    public string FilterTeam { get; private set; } = "";

    public string FilterQ { get; private set; } = "";

    public string NameValue { get; private set; } = "";

    public string GroupValue { get; private set; } = "";

    private static string BadgeOf(UserRow u) =>
        u.TeamStatus == "multiple" ? "In several teams — check Entra groups"
        : u.TeamStatus == "overage" ? "Too many groups — team not applied"
        : u.TeamStatus == "none" || u.TeamId is null ? "No team — sees own records only" : "";

    private async Task LoadAsync()
    {
        Setup("users", "Users & teams");
        ConnectionIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var q = Request.Query;
        string Q(string k) => q.TryGetValue(k, out var v) ? v.ToString() : "";
        FilterRole = Q("role") is "Admin" or "Manager" or "User" ? Q("role") : "";
        FilterTeam = Q("team");
        FilterQ = Q("q").Trim().ToLowerInvariant();
        if (FilterQ.Length > 100) { FilterQ = FilterQ[..100]; }
        var (users, teams, lk) = await RunAsync(async (admin, repo) => (await admin.UsersAsync(), await admin.TeamsAsync(), await repo.LookupsAsync()));
        var f = new Formatter(lk.Settings.LocaleCode, lk.Settings.Timezone);
        Teams = teams;
        var filtered = users.Where(u => (FilterRole.Length == 0 || u.Role == FilterRole)
            && (FilterTeam.Length == 0 || (FilterTeam == "none" ? u.TeamId is null : u.TeamId?.ToString(CultureInfo.InvariantCulture) == FilterTeam))
            && (FilterQ.Length == 0 || u.Name.ToLowerInvariant().Contains(FilterQ, StringComparison.Ordinal) || (u.Email ?? "").ToLowerInvariant().Contains(FilterQ, StringComparison.Ordinal)));
        Users = filtered.Select(u => new UserVm(u, u.LastSignIn is null ? "" : f.DateTime(u.LastSignIn), BadgeOf(u))).ToList();
        NoTeamCount = users.Count(u => u.Active && BadgeOf(u).Length > 0);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? name, string? groupId)
    {
        var (n, g, errors) = SettingsValidators.ValidateTeam(name, groupId);
        var meta = MetaOf();
        if (errors.Count == 0)
        {
            var r = await RunAsync(async (admin, _) => await admin.SaveTeamAsync(null, n, g, meta));
            if (r.Ok)
            {
                Flash($"Team {n} added.");
                return SeeOther("/settings/users");
            }
            errors[r.Field ?? "name"] = r.Error!;
        }
        await LoadAsync();
        Errors = errors;
        NameValue = name ?? "";
        GroupValue = groupId ?? "";
        return Invalid();
    }
}
