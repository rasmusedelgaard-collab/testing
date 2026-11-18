using System.Text.Json;
using Crm.Web.Auth;
using Crm.Web.Services;
using Dapper;

namespace Crm.Web.Data;

public sealed record TestUserRow(int UserId, string Oid, string Name, string? Email, CrmRole Role, int? TeamId, string? TeamName);

/// <summary>Test-access-code mode only (ports of testUserRepo.ts). Go-live removal list: see dotnet.md 8.3.</summary>
public sealed partial class TestUserRepo(SystemDb db)
{
    private sealed record Raw(int UserId, string EntraOid, string DisplayName, string? Email, string Role, int? TeamId, string? TeamName);

    private const string Base = """
        SELECT u.UserId, CONVERT(char(36), u.EntraOid) AS EntraOid, u.DisplayName, u.Email, u.Role, u.TeamId, t.Name AS TeamName
        FROM crm.Users u LEFT JOIN crm.Teams t ON t.TeamId = u.TeamId WHERE u.IsActive = 1
        """;
    private const string Reserved = "LOWER(CONVERT(char(36), u.EntraOid)) LIKE '00000000-0000-4000-8000-%'";

    [System.Text.RegularExpressions.GeneratedRegex(@"^00000000-0000-4000-8000-[0-9a-f]{12}\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ReservedOid();

    private static TestUserRow Map(Raw r) => new(r.UserId, r.EntraOid.ToLowerInvariant(), r.DisplayName, r.Email, Enum.Parse<CrmRole>(r.Role), r.TeamId, r.TeamName);

    private static DynamicParameters OidsParam(IReadOnlyList<string> oids)
    {
        var p = new DynamicParameters();
        p.Add("oids", JsonSerializer.Serialize(oids), System.Data.DbType.String, size: -1);
        return p;
    }

    /// <summary>The seeded users plus any active user in the reserved test-oid range (the optional named Admin).</summary>
    public async Task<IReadOnlyList<TestUserRow>> ListAsync(CancellationToken ct)
    {
        var rows = await db.QueryAsync<Raw>(
            $"{Base} AND (u.EntraOid IN (SELECT TRY_CAST([value] AS uniqueidentifier) FROM OPENJSON(@oids)) OR {Reserved}) ORDER BY u.UserId",
            OidsParam(TestUsers.SeedOids), ct).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<TestUserRow?> FindAsync(string oid, CancellationToken ct)
    {
        if (!TestUsers.SeedOids.Contains(oid) && !ReservedOid().IsMatch(oid))
        {
            return null;
        }
        var p = OidsParam(TestUsers.SeedOids);
        p.Add("oid", oid, System.Data.DbType.AnsiString, size: 36);
        var rows = await db.QueryAsync<Raw>(
            $"{Base} AND u.EntraOid = TRY_CAST(@oid AS uniqueidentifier) AND (u.EntraOid IN (SELECT TRY_CAST([value] AS uniqueidentifier) FROM OPENJSON(@oids)) OR {Reserved})",
            p, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : Map(rows[0]);
    }
}
