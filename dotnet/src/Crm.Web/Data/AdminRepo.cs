using System.Data;
using Crm.Web.Domain;
using Dapper;

namespace Crm.Web.Data;

public sealed record TeamRow(int Id, string Name, string GroupId, bool Active, int Members);

public sealed record UserRow(int Id, string Name, string? Email, string Role, int? TeamId, string? TeamName, string TeamStatus, string? LastSignIn, bool Active);

public sealed record SaveOutcomeAdmin(bool Ok, string? Error = null, string? Field = null, int Id = 0);

/// <summary>Admin configuration SQL (port of sqlAdmin.ts, increment 4 subset). Runs in the Admin's RLS transaction; fn_AdminWrite is the database backstop.</summary>
public sealed partial class AdminRepo(RlsTx tx)
{
    private static DynamicParameters P() => new();

    private sealed record KeyValueRow(string K, string V);

    private sealed record SettingRow(int SettingId, string SettingValue);

    /// <summary>Config audit rows: OwnerId = ChangedBy and TeamId NULL (overview 7).</summary>
    private async Task AuditAsync(string type, int id, string action, string? field, string? oldV, string? newV, Meta meta)
    {
        var p = P();
        p.Add("et", type, DbType.AnsiString, size: 20);
        p.Add("eid", id, DbType.Int32);
        p.Add("act", action, DbType.AnsiStringFixedLength, size: 1);
        p.Add("fn", field, DbType.AnsiString, size: 64);
        p.Add("ov", oldV?.Length > 4000 ? oldV[..4000] : oldV, DbType.String, size: 4000);
        p.Add("nv", newV?.Length > 4000 ? newV[..4000] : newV, DbType.String, size: 4000);
        p.Add("by", meta.UserId, DbType.Int32);
        p.Add("cid", meta.CorrelationId, DbType.Guid);
        await tx.ExecuteAsync(
            """
            INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId)
              VALUES (@et, @eid, @act, @fn, @ov, @nv, @by, NULL, @by, @cid)
            """, p).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> SettingsMapAsync() =>
        (await tx.QueryAsync<KeyValueRow>("SELECT SettingKey AS K, SettingValue AS V FROM crm.Settings").ConfigureAwait(false)).ToDictionary(r => r.K, r => r.V, StringComparer.Ordinal);

    public async Task SaveSettingsAsync(IReadOnlyDictionary<string, string> updates, Meta meta)
    {
        foreach (var (key, value) in updates)
        {
            var p = P();
            p.Add("k", key, DbType.AnsiString, size: 64);
            var cur = await tx.QuerySingleOrDefaultAsync<SettingRow>("SELECT SettingId, SettingValue FROM crm.Settings WHERE SettingKey = @k", p).ConfigureAwait(false);
            p.Add("v", value, DbType.String, size: 1000);
            p.Add("by", meta.UserId, DbType.Int32);
            if (cur is null)
            {
                var id = await tx.ExecuteScalarAsync<int>("INSERT crm.Settings (SettingKey, SettingValue, ModifiedBy) OUTPUT inserted.SettingId VALUES (@k, @v, @by)", p).ConfigureAwait(false);
                await AuditAsync("setting", id, "C", key, null, value, meta).ConfigureAwait(false);
            }
            else if (cur.SettingValue != value)
            {
                p.Add("id", cur.SettingId, DbType.Int32);
                await tx.ExecuteAsync("UPDATE crm.Settings SET SettingValue = @v, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE SettingId = @id", p).ConfigureAwait(false);
                await AuditAsync("setting", cur.SettingId, "U", key, cur.SettingValue, value, meta).ConfigureAwait(false);
            }
        }
    }

    public async Task<string?> LogoShaAsync()
    {
        var rows = await tx.QueryAsync<string>("SELECT LOWER(CONVERT(char(64), Sha256, 2)) FROM crm.BrandingAssets WHERE AssetKey = 'logo'").ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task SaveLogoAsync(byte[] content, byte[] sha256, string shaHex, Meta meta)
    {
        var p = P();
        p.Add("c", content, DbType.Binary, size: -1);
        p.Add("sha", sha256, DbType.Binary, size: 32);
        p.Add("by", meta.UserId, DbType.Int32);
        var updated = await tx.QueryAsync<int>(
            "UPDATE crm.BrandingAssets SET ContentType = 'image/png', Content = @c, Sha256 = @sha, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() OUTPUT inserted.AssetId WHERE AssetKey = 'logo'", p).ConfigureAwait(false);
        if (updated.Count > 0)
        {
            await AuditAsync("branding", updated[0], "U", "logo", null, shaHex, meta).ConfigureAwait(false);
            return;
        }
        var id = await tx.ExecuteScalarAsync<int>("INSERT crm.BrandingAssets (AssetKey, ContentType, Content, Sha256, ModifiedBy) OUTPUT inserted.AssetId VALUES ('logo', 'image/png', @c, @sha, @by)", p).ConfigureAwait(false);
        await AuditAsync("branding", id, "C", "logo", null, shaHex, meta).ConfigureAwait(false);
    }

    private const string TeamSelect = "SELECT t.TeamId AS Id, t.Name, LOWER(CONVERT(char(36), t.EntraGroupId)) AS GroupId, t.IsActive AS Active, (SELECT COUNT(*) FROM crm.Users u WHERE u.TeamId = t.TeamId) AS Members FROM crm.Teams t";

    public async Task<IReadOnlyList<TeamRow>> TeamsAsync() => await tx.QueryAsync<TeamRow>($"{TeamSelect} ORDER BY t.Name").ConfigureAwait(false);

    public Task<TeamRow?> TeamAsync(int id) => tx.QuerySingleOrDefaultAsync<TeamRow>($"{TeamSelect} WHERE t.TeamId = @id", new { id });

    public async Task<SaveOutcomeAdmin> SaveTeamAsync(int? id, string name, string groupId, Meta meta)
    {
        var all = await TeamsAsync().ConfigureAwait(false);
        if (all.FirstOrDefault(t => t.Id != id && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) is { } nameDup)
        {
            return new SaveOutcomeAdmin(false, $"A team named {nameDup.Name} already exists.", "name");
        }
        if (all.FirstOrDefault(t => t.Id != id && t.GroupId == groupId) is { } groupDup)
        {
            return new SaveOutcomeAdmin(false, $"This group is already linked to the team {groupDup.Name}.", "groupId");
        }
        var p = P();
        p.Add("n", name, DbType.String, size: 80);
        p.Add("g", Guid.Parse(groupId), DbType.Guid);
        p.Add("by", meta.UserId, DbType.Int32);
        if (id is null)
        {
            var newId = await tx.ExecuteScalarAsync<int>("INSERT crm.Teams (Name, EntraGroupId, ModifiedBy) OUTPUT inserted.TeamId VALUES (@n, @g, @by)", p).ConfigureAwait(false);
            await AuditAsync("team", newId, "C", null, null, name, meta).ConfigureAwait(false);
            return new SaveOutcomeAdmin(true, Id: newId);
        }
        var old = all.FirstOrDefault(t => t.Id == id);
        if (old is null)
        {
            return new SaveOutcomeAdmin(false, "Team not found.");
        }
        p.Add("id", id.Value, DbType.Int32);
        await tx.ExecuteAsync("UPDATE crm.Teams SET Name = @n, EntraGroupId = @g, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE TeamId = @id", p).ConfigureAwait(false);
        if (old.Name != name) { await AuditAsync("team", id.Value, "U", "Name", old.Name, name, meta).ConfigureAwait(false); }
        if (old.GroupId != groupId) { await AuditAsync("team", id.Value, "U", "EntraGroupId", old.GroupId, groupId, meta).ConfigureAwait(false); }
        return new SaveOutcomeAdmin(true, Id: id.Value);
    }

    public async Task<TeamRow?> SetTeamActiveAsync(int id, bool active, Meta meta)
    {
        var old = await TeamAsync(id).ConfigureAwait(false);
        if (old is null)
        {
            return null;
        }
        if (old.Active != active)
        {
            var p = P();
            p.Add("a", active, DbType.Boolean);
            p.Add("by", meta.UserId, DbType.Int32);
            p.Add("id", id, DbType.Int32);
            await tx.ExecuteAsync("UPDATE crm.Teams SET IsActive = @a, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE TeamId = @id", p).ConfigureAwait(false);
            await AuditAsync("team", id, "U", "IsActive", old.Active ? "1" : "0", active ? "1" : "0", meta).ConfigureAwait(false);
        }
        return old;
    }

    public async Task<IReadOnlyList<UserRow>> UsersAsync() =>
        await tx.QueryAsync<UserRow>(
            """
            SELECT u.UserId AS Id, u.DisplayName AS Name, u.Email, u.Role, u.TeamId, t.Name AS TeamName, u.TeamStatus, CONVERT(varchar(23), u.LastSignInAt, 126) + 'Z' AS LastSignIn, u.IsActive AS Active
              FROM crm.Users u LEFT JOIN crm.Teams t ON t.TeamId = u.TeamId ORDER BY u.DisplayName
            """).ConfigureAwait(false);
}
