using System.Text.Json;
using Crm.Web.Auth;

namespace Crm.Web.Data;

/// <summary>The only write path to crm.Users: crm.usp_UpsertUserOnLogin (idempotent MERGE, so retry-safe), plus the IsActive re-check.</summary>
public sealed class UserRepo(SystemDb db)
{
    /// <summary>Error number raised by crm.usp_UpsertUserOnLogin for a deactivated user.</summary>
    public const int UserDeactivated = 50011;

    private sealed record UpsertRow(int UserId, int? TeamId, string TeamStatus, int TeamMatches);

    public async Task<bool> IsActiveAsync(int userId, CancellationToken ct)
    {
        var rows = await db.QueryAsync<bool>("SELECT IsActive FROM crm.Users WHERE UserId = @id", new { id = userId }, ct).ConfigureAwait(false);
        return rows.Count == 1 && rows[0];
    }

    public async Task<UpsertResult> UpsertOnLoginAsync(string oid, string tenantId, string displayName, string? email, CrmRole role, GroupsInfo groups, CancellationToken ct)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("EntraOid", Guid.Parse(oid), System.Data.DbType.Guid);
        p.Add("TenantId", Guid.Parse(tenantId), System.Data.DbType.Guid);
        p.Add("DisplayName", displayName, System.Data.DbType.String, size: 200);
        p.Add("Email", email, System.Data.DbType.String, size: 254);
        p.Add("Role", role.ToString(), System.Data.DbType.AnsiString, size: 10);
        p.Add("GroupIdsJson", JsonSerializer.Serialize(groups.GroupIds), System.Data.DbType.String, size: -1);
        p.Add("GroupsOverage", groups.Overage, System.Data.DbType.Boolean);
        var rows = await db.QueryAsync<UpsertRow>(
            """
            EXEC crm.usp_UpsertUserOnLogin @EntraOid = @EntraOid, @TenantId = @TenantId, @DisplayName = @DisplayName,
              @Email = @Email, @Role = @Role, @GroupIdsJson = @GroupIdsJson, @GroupsOverage = @GroupsOverage
            """, p, ct).ConfigureAwait(false);
        var row = rows.FirstOrDefault() ?? throw new InvalidOperationException("usp_UpsertUserOnLogin returned no row");
        var status = row.TeamStatus switch { "ok" => TeamStatus.Ok, "multiple" => TeamStatus.Multiple, "overage" => TeamStatus.Overage, _ => TeamStatus.None };
        return new UpsertResult(row.UserId, row.TeamId, status, row.TeamMatches);
    }
}
