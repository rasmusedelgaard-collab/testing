using System.Globalization;
using Crm.Web.Config;
using Crm.Web.Data;
using Crm.Web.Domain;
using Crm.Web.Infrastructure;
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

    private static async Task<(IReadOnlyList<UserRow> Users, IReadOnlyList<TeamRow> Teams, Lookups Lk)> ReadAsync(AdminRepo admin, CrmRepo repo) =>
        (await admin.UsersAsync(), await admin.TeamsAsync(), await repo.LookupsAsync());

    private void Apply((IReadOnlyList<UserRow> Users, IReadOnlyList<TeamRow> Teams, Lookups Lk) data)
    {
        Setup("users", "Users & teams");
        ConnectionIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var q = Request.Query;
        string Q(string k) => q.TryGetValue(k, out var v) ? v.ToString() : "";
        FilterRole = Q("role") is "Admin" or "Manager" or "User" ? Q("role") : "";
        FilterTeam = Q("team");
        FilterQ = Q("q").Trim().ToLowerInvariant();
        if (FilterQ.Length > 100) { FilterQ = FilterQ[..100]; }
        var f = new Formatter(data.Lk.Settings.LocaleCode, data.Lk.Settings.Timezone);
        Teams = data.Teams;
        var filtered = data.Users.Where(u => (FilterRole.Length == 0 || u.Role == FilterRole)
            && (FilterTeam.Length == 0 || (FilterTeam == "none" ? u.TeamId is null : u.TeamId?.ToString(CultureInfo.InvariantCulture) == FilterTeam))
            && (FilterQ.Length == 0 || u.Name.ToLowerInvariant().Contains(FilterQ, StringComparison.Ordinal) || (u.Email ?? "").ToLowerInvariant().Contains(FilterQ, StringComparison.Ordinal)));
        Users = filtered.Select(u => new UserVm(u, u.LastSignIn is null ? "" : f.DateTime(u.LastSignIn), BadgeOf(u))).ToList();
        NoTeamCount = data.Users.Count(u => u.Active && BadgeOf(u).Length > 0);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Request.Path.Value!.TrimEnd('/').EndsWith("/users", StringComparison.OrdinalIgnoreCase)) { throw new NotFoundException(); } // /settings/teams is POST-only
        Apply(await RunAsync(ReadAsync));
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? name, string? groupId)
    {
        var (n, g, errors) = SettingsValidators.ValidateTeam(name, groupId);
        var meta = MetaOf();
        // One transaction per request: the save (if valid) and the data for re-rendering the page share it.
        var (saved, data) = await RunAsync(async (admin, repo) =>
        {
            SaveOutcomeAdmin? r = errors.Count == 0 ? await admin.SaveTeamAsync(null, n, g, meta) : null;
            return (r, await ReadAsync(admin, repo));
        });
        if (saved is { Ok: true })
        {
            Flash($"Team {n} added.");
            return SeeOther("/settings/users");
        }
        if (saved is not null) { errors[saved.Field ?? "name"] = saved.Error!; }
        Apply(data);
        Errors = errors;
        NameValue = name ?? "";
        GroupValue = groupId ?? "";
        return Invalid();
    }
}
