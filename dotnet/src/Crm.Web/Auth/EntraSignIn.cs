using System.Security.Claims;
using System.Text.Json;
using Crm.Web.Data;
using Microsoft.Extensions.Logging;

namespace Crm.Web.Auth;

public enum SignInOutcome { Ok, TenantMismatch, NoAccess, Deactivated }

public sealed record SignInResult(SignInOutcome Outcome, ClaimsPrincipal? Principal);

public sealed record GroupsInfo(IReadOnlyList<string> GroupIds, bool Overage);

public enum TeamStatus { Ok, None, Multiple, Overage }

public sealed record UpsertResult(int UserId, int? TeamId, TeamStatus TeamStatus, int TeamMatches);

/// <summary>Entra OnTokenValidated logic, unit-testable without HTTP (ports of roles.ts, userSync.ts).</summary>
public sealed class EntraSignIn(UserRepo users, TimeProvider time, ILogger<EntraSignIn> log)
{
    public static GroupsInfo ExtractGroups(IReadOnlyList<Claim> claims)
    {
        var groups = claims.Where(c => c.Type == "groups").Select(c => c.Value).ToList();
        var overage = ClaimNamesContainsGroups(claims.FirstOrDefault(c => c.Type == "_claim_names")?.Value)
            || (groups.Count == 0 && !claims.Any(c => c.Type == "groups") && claims.Any(c => c.Type == "hasgroups" && string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase)));
        return overage ? new GroupsInfo([], true) : new GroupsInfo(groups.Select(g => g.ToLowerInvariant()).ToList(), false);
    }

    private static bool ClaimNamesContainsGroups(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("groups", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static int? ApplyTeamPolicy(UpsertResult r, string oid, ILogger log)
    {
        switch (r.TeamStatus)
        {
            case TeamStatus.Ok:
                return r.TeamMatches == 1 ? r.TeamId : null;
            case TeamStatus.Multiple:
                log.LogWarning("user {Oid} in several team groups ({Count}); no team applied", oid, r.TeamMatches);
                return null;
            case TeamStatus.Overage:
                log.LogError("groups claim overage for user {Oid}; no team applied", oid);
                return null;
            default:
                return null;
        }
    }

    public async Task<SignInResult> ProcessAsync(IReadOnlyList<Claim> claims, string expectedTenantId, CancellationToken ct)
    {
        string? One(string t) => claims.FirstOrDefault(c => c.Type == t)?.Value;
        var tid = One("tid") ?? One("http://schemas.microsoft.com/identity/claims/tenantid");
        var oid = (One("oid") ?? One("http://schemas.microsoft.com/identity/claims/objectidentifier"))?.ToLowerInvariant();
        if (tid is null || !string.Equals(tid, expectedTenantId, StringComparison.OrdinalIgnoreCase) || oid is null || !Guid.TryParse(oid, out _))
        {
            return new SignInResult(SignInOutcome.TenantMismatch, null);
        }
        var role = Roles.Map(claims.Where(c => c.Type is "roles" or ClaimTypes.Role).Select(c => c.Value));
        if (role is null)
        {
            log.LogInformation("sign-in without a CRM app role (oid {Oid})", oid);
            return new SignInResult(SignInOutcome.NoAccess, null);
        }
        var groups = ExtractGroups(claims);
        var name = One("name") ?? One("preferred_username") ?? One("email") ?? "Unknown user";
        var email = One("email") ?? One("preferred_username");
        UpsertResult result;
        try
        {
            result = await users.UpsertOnLoginAsync(oid, tid, Truncate(name, 200), email is null ? null : Truncate(email, 254), role.Value, groups, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (Transient.HasNumber(e, UserRepo.UserDeactivated))
        {
            log.LogWarning("sign-in refused: user {Oid} is deactivated", oid);
            return new SignInResult(SignInOutcome.Deactivated, null);
        }
        var team = ApplyTeamPolicy(result, oid, log);
        var user = new SessionUser(result.UserId, oid, Truncate(name, 200), email, role.Value, team, time.GetUtcNow().ToUnixTimeMilliseconds());
        return new SignInResult(SignInOutcome.Ok, CrmPrincipal.Create(user));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
