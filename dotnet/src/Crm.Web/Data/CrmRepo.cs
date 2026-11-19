using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Crm.Web.Domain;
using Crm.Web.Services;
using Dapper;

namespace Crm.Web.Data;

/// <summary>All CRM SQL for the four entities (port of sqlRepo.ts). One instance per request around the RlsTx. Parameterised SQL only.</summary>
public sealed partial class CrmRepo(RlsTx tx, RlsContext ctx)
{
    [GeneratedRegex(@"^[A-Za-z0-9+/]{11}=\z", RegexOptions.CultureInvariant)]
    private static partial Regex RowVerPattern();

    /// <summary>The rowversion travels in the hidden form field as base64 of its 8 bytes.</summary>
    public static bool IsRowVer(string s) => RowVerPattern().IsMatch(s);

    private static DynamicParameters P() => new();

    /// <summary>Every LIKE wildcard and the escape character itself is escaped as a pair, so the pattern never ends in a lone escape.</summary>
    internal static string EscapeLike(string s) => Regex.Replace(s, @"[\\%_\[]", m => "\\" + m.Value);

    private static void AddColumn(DynamicParameters p, ColumnSpec c, object? value)
    {
        switch (c.Type)
        {
            case ColType.Text:
                if (c.Name is "CountryCode" or "CurrencyCode" or "ActivityType")
                {
                    p.Add(c.Name, value, DbType.AnsiString, size: c.Length);
                }
                else
                {
                    p.Add(c.Name, value, DbType.String, size: c.Length);
                }
                break;
            case ColType.Int: p.Add(c.Name, value, DbType.Int32); break;
            case ColType.Date: p.Add(c.Name, value, DbType.AnsiString, size: 10); break;
            case ColType.Time: p.Add(c.Name, value, DbType.AnsiString, size: 8); break;
            case ColType.Money: p.Add(c.Name, value, DbType.Decimal, precision: 19, scale: 2); break;
            default: p.Add(c.Name, value ?? false, DbType.Boolean); break;
        }
    }

    public async Task<Lookups> LookupsAsync() =>
        await tx.QueryMultipleAsync(
            """
            SELECT SettingKey, SettingValue FROM crm.Settings;
            SELECT StageId, Name, SortOrder, Probability, StageType FROM crm.PipelineStages ORDER BY SortOrder;
            SELECT p.Code AS PCode, v.ValueId, v.Code, v.Label, v.IsActive FROM crm.PicklistValues v JOIN crm.Picklists p ON p.PicklistId = v.PicklistId ORDER BY p.Code, v.SortOrder, v.Label;
            """, null,
            async g =>
            {
                var kv = (await g.ReadAsync()).ToDictionary(r => (string)r.SettingKey, r => (string)r.SettingValue);
                string Get(string k, string d) => kv.TryGetValue(k, out var v) ? v : d;
                int GetInt(string k) => int.TryParse(Get(k, "1"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n != 0 ? n : 1;
                var settings = new Settings(
                    Get("app.name", "CRM"), Get("locale.code", "en-GB"), Get("locale.timezone", "UTC"), Get("currency.base", "DKK"),
                    Get("currency.enabled", "DKK").Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList(),
                    Get("visibility.mode", "private") == "team" ? "team" : "private",
                    Get("branding.accentColor", "#00456C"), GetInt("locale.firstDayOfWeek"), GetInt("fiscal.startMonth"));
                var stages = (await g.ReadAsync()).Select(r => new Stage((int)r.StageId, (string)r.Name, (int)r.SortOrder, Convert.ToInt32(r.Probability, CultureInfo.InvariantCulture), (string)r.StageType)).ToList();
                var picks = new Dictionary<string, List<PicklistValue>>(StringComparer.Ordinal);
                foreach (var r in await g.ReadAsync())
                {
                    string code = r.PCode;
                    if (!picks.TryGetValue(code, out var l)) { picks[code] = l = []; }
                    l.Add(new PicklistValue((int)r.ValueId, (string?)r.Code, (string)r.Label, (bool)r.IsActive));
                }
                return new Lookups(settings, stages, picks.ToDictionary(k => k.Key, k => (IReadOnlyList<PicklistValue>)k.Value, StringComparer.Ordinal));
            }).ConfigureAwait(false);

    private const string OwnerSelect = "SELECT u.UserId AS Id, u.DisplayName AS Name, t.Name AS TeamName, u.IsActive AS Active FROM crm.Users u LEFT JOIN crm.Teams t ON t.TeamId = u.TeamId";

    private sealed record OwnerRow(int Id, string Name, string? TeamName, bool Active);

    private static OwnerOption Map(OwnerRow r) => new(r.Id, r.Name, r.TeamName, r.Active);

    /// <summary>Admin: every active user. Others: self plus ACTIVE members of their own team (the rule fn_RowWriteTarget enforces).</summary>
    public async Task<IReadOnlyList<OwnerOption>> OwnersAsync()
    {
        IReadOnlyList<OwnerRow> rows;
        if (ctx.Role == CrmRole.Admin)
        {
            rows = await tx.QueryAsync<OwnerRow>($"{OwnerSelect} WHERE u.IsActive = 1 ORDER BY u.DisplayName").ConfigureAwait(false);
        }
        else
        {
            rows = await tx.QueryAsync<OwnerRow>($"{OwnerSelect} WHERE u.UserId = @me OR (u.IsActive = 1 AND @team IS NOT NULL AND u.TeamId = @team) ORDER BY u.DisplayName", new { me = ctx.UserId, team = ctx.TeamId }).ConfigureAwait(false);
        }
        return rows.Select(Map).ToList();
    }

    public async Task<OwnerOption?> OwnerByIdAsync(int id)
    {
        var r = await tx.QuerySingleOrDefaultAsync<OwnerRow>($"{OwnerSelect} WHERE u.UserId = @id", new { id }).ConfigureAwait(false);
        return r is null ? null : Map(r);
    }

    private sealed record RefRow(int Id, string Label);

    public async Task<IReadOnlyList<RefOption>> RefOptionsAsync(string entity)
    {
        var d = EntityDefinitions.All[entity];
        var label = entity == "contacts" ? "LTRIM(CONCAT(FirstName, N' ', LastName))" : entity == "activities" ? "Subject" : "Name";
        var rows = await tx.QueryAsync<RefRow>($"SELECT TOP (500) {d.Pk} AS Id, {label} AS Label FROM {d.Table} ORDER BY {label}").ConfigureAwait(false);
        return rows.Select(r => new RefOption(r.Id, r.Label)).ToList();
    }

    public async Task<(IReadOnlyList<Row> Rows, int Total)> ListAsync(string entity, ListQuery q)
    {
        var d = EntityDefinitions.All[entity];
        var sql = CrmSql.Entities[entity];
        var from = $"FROM {d.Table} e {CrmSql.CommonJoins} {sql.Joins}";
        var p = P();
        p.Add("today", q.Today, DbType.AnsiString, size: 10);
        var where = new List<string>();
        foreach (var f in d.Filters)
        {
            if (!q.Filters.TryGetValue(f.Key, out var value))
            {
                continue;
            }
            var clause = CrmSql.FilterClause(entity, f.Key, value);
            if (clause is null)
            {
                continue;
            }
            where.Add(clause);
            if (clause.Contains($"@f_{f.Key}", StringComparison.Ordinal))
            {
                if (f.Kind == FilterKind.Int) { p.Add($"f_{f.Key}", int.Parse(value, CultureInfo.InvariantCulture), DbType.Int32); }
                else { p.Add($"f_{f.Key}", value, DbType.AnsiString, size: 10); }
            }
        }
        if (q.Q.Length > 0)
        {
            where.Add("(" + string.Join(" OR ", d.Search.Select(s => $"{s.Sql} LIKE @q ESCAPE '\\'")) + ")");
            p.Add("q", $"%{EscapeLike(q.Q)}%", DbType.String, size: 4000); // escaping can double the length: never truncate (a cut could leave a lone escape)
        }
        // Sort comes from the whitelist by exact key match; an unknown key falls back to the default.
        var sort = d.Sorts.TryGetValue(q.Sort, out var s0) ? s0 : d.Sorts[d.DefaultSort];
        var dir = q.Dir == "desc" ? "DESC" : "ASC";
        var w = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        p.Add("off", (q.Page - 1) * q.PageSize, DbType.Int32);
        p.Add("ps", q.PageSize, DbType.Int32);
        var select = $"SELECT e.{d.Pk} AS Id, {sql.Cols}, {CrmSql.CommonCols}";
        return await tx.QueryMultipleAsync(
            $"""
            SELECT COUNT(*) AS Total {from} {w};
            {select} {from} {w} ORDER BY {sort.Sql} {dir}, e.{d.Pk} {dir} OFFSET @off ROWS FETCH NEXT @ps ROWS ONLY;
            """, p,
            async g =>
            {
                var total = await g.ReadSingleAsync<int>().ConfigureAwait(false);
                var rows = (await g.ReadAsync().ConfigureAwait(false)).Select(r => new Row((IDictionary<string, object?>)r)).ToList();
                return ((IReadOnlyList<Row>)rows, total);
            }).ConfigureAwait(false);
    }

    public async Task<Row?> GetAsync(string entity, int id)
    {
        var d = EntityDefinitions.All[entity];
        var sql = CrmSql.Entities[entity];
        var rows = await tx.QueryRowsAsync($"SELECT e.{d.Pk} AS Id, {sql.Cols}, {CrmSql.CommonCols} FROM {d.Table} e {CrmSql.CommonJoins} {sql.Joins} WHERE e.{d.Pk} = @id", new { id }).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<bool> RefExistsAsync(string entity, int id)
    {
        var d = EntityDefinitions.All[entity];
        return (await tx.QueryAsync<int>($"SELECT 1 FROM {d.Table} WHERE {d.Pk} = @id", new { id }).ConfigureAwait(false)).Count > 0;
    }

    private sealed record HistRow(string At, string ByName, string Action, string? FieldName, string? OldValue, string? NewValue);

    /// <summary>Audit trail of one record, newest first (owner ids resolved to names).</summary>
    public async Task<IReadOnlyList<HistoryRow>> HistoryAsync(string entity, int id)
    {
        string Owner(string c) => $"CASE WHEN a.FieldName = 'OwnerId' THEN (SELECT DisplayName FROM crm.Users WHERE UserId = TRY_CAST(a.{c} AS int)) ELSE a.{c} END";
        var p = P();
        p.Add("et", EntityDefinitions.All[entity].AuditType, DbType.AnsiString, size: 20);
        p.Add("id", id, DbType.Int32);
        var rows = await tx.QueryAsync<HistRow>(
            $"""
            SELECT TOP (100) CONVERT(varchar(23), a.ChangedAt, 126) + 'Z' AS At, u.DisplayName AS ByName, a.Action, a.FieldName,
                {Owner("OldValue")} AS OldValue, {Owner("NewValue")} AS NewValue
              FROM crm.AuditLog a JOIN crm.Users u ON u.UserId = a.ChangedBy
              WHERE a.EntityType = @et AND a.EntityId = @id ORDER BY a.ChangedAt DESC, a.AuditId DESC
            """, p).ConfigureAwait(false);
        return rows.Select(r => new HistoryRow(r.At, r.ByName, r.Action.Trim(), r.FieldName, r.OldValue, r.NewValue)).ToList();
    }

    private static DynamicParameters ValueParams(EntityDef d, IReadOnlyDictionary<string, object?> values)
    {
        var p = P();
        foreach (var c in d.Columns)
        {
            AddColumn(p, c, values.GetValueOrDefault(c.Name));
        }
        p.Add("OwnerId", values.GetValueOrDefault("OwnerId"), DbType.Int32);
        return p;
    }

    private async Task WriteAuditAsync(IEnumerable<AuditRow> rows, Meta meta)
    {
        foreach (var a in rows)
        {
            var p = P();
            p.Add("et", a.EntityType, DbType.AnsiString, size: 20);
            p.Add("eid", a.EntityId, DbType.Int32);
            p.Add("act", a.Action, DbType.AnsiStringFixedLength, size: 1);
            p.Add("fn", a.FieldName, DbType.AnsiString, size: 64);
            p.Add("ov", a.OldValue, DbType.String, size: 4000);
            p.Add("nv", a.NewValue, DbType.String, size: 4000);
            p.Add("owner", a.OwnerId, DbType.Int32);
            p.Add("team", a.TeamId, DbType.Int32);
            p.Add("by", meta.UserId, DbType.Int32);
            p.Add("cid", meta.CorrelationId, DbType.Guid);
            await tx.ExecuteAsync(
                """
                INSERT crm.AuditLog (EntityType, EntityId, Action, FieldName, OldValue, NewValue, OwnerId, TeamId, ChangedBy, CorrelationId)
                  VALUES (@et, @eid, @act, @fn, @ov, @nv, @owner, @team, @by, @cid)
                """, p).ConfigureAwait(false);
        }
    }

    public async Task<int> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, Meta meta)
    {
        var d = EntityDefinitions.All[entity];
        var x = CrmSql.Entities[entity].InsertExtra;
        var names = d.Columns.Select(c => c.Name).Concat(["OwnerId", "TeamId", "CreatedBy", "ModifiedBy"]).Concat(x is null ? [] : [x.Value.Col]);
        var vals = d.Columns.Select(c => "@" + c.Name).Concat(["@OwnerId", "@TeamId", "@By", "@By"]).Concat(x is null ? [] : [x.Value.Expr]);
        var p = ValueParams(d, values);
        p.Add("By", meta.UserId, DbType.Int32);
        // TeamId is always derived from the owner in SQL; it is never bound from a form.
        var rows = await tx.QueryRowsAsync(
            $"""
            DECLARE @TeamId int = (SELECT TeamId FROM crm.Users WHERE UserId = @OwnerId);
            INSERT {d.Table} ({string.Join(", ", names)}) OUTPUT inserted.{d.Pk} AS Id, inserted.OwnerId, inserted.TeamId VALUES ({string.Join(", ", vals)});
            """, p).ConfigureAwait(false);
        var r = rows.FirstOrDefault() ?? throw new InvalidOperationException("insert returned no row");
        var id = r.Int("Id");
        await WriteAuditAsync([Audit.Create(d, id, Convert.ToString(values.GetValueOrDefault(d.LabelCol), CultureInfo.InvariantCulture) ?? "", r.Int("OwnerId"), r.IntOrNull("TeamId"))], meta).ConfigureAwait(false);
        return id;
    }

    public async Task<UpdateResult> UpdateAsync(string entity, int id, IReadOnlyDictionary<string, object?> values, string rowVer, Meta meta)
    {
        var d = EntityDefinitions.All[entity];
        if (!IsRowVer(rowVer))
        {
            return UpdateResult.Conflict;
        }
        var x = CrmSql.Entities[entity].UpdateExtra;
        var sets = d.Columns.Select(c => $"{c.Name} = @{c.Name}")
            .Concat(["OwnerId = @OwnerId", "TeamId = (SELECT TeamId FROM crm.Users WHERE UserId = @OwnerId)", "ModifiedBy = @By", "ModifiedAt = SYSUTCDATETIME()"])
            .Concat(x is null ? [] : [$"{x.Value.Col} = {x.Value.Expr}"]);
        var output = d.AuditFields.Distinct().SelectMany(f => f == "CloseDate"
            ? new[] { "CONVERT(char(10), deleted.CloseDate, 23) AS o_CloseDate", "CONVERT(char(10), inserted.CloseDate, 23) AS n_CloseDate" }
            : [$"deleted.{f} AS o_{f}", $"inserted.{f} AS n_{f}"]);
        var p = ValueParams(d, values);
        p.Add("By", meta.UserId, DbType.Int32);
        p.Add("id", id, DbType.Int32);
        p.Add("rv", Convert.FromBase64String(rowVer), DbType.Binary, size: 8);
        var rows = await tx.QueryRowsAsync(
            $"""
            UPDATE {d.Table} SET {string.Join(", ", sets)} OUTPUT {string.Join(", ", output)}, inserted.TeamId AS n_TeamId
              WHERE {d.Pk} = @id AND RowVer = @rv
            """, p).ConfigureAwait(false);
        var r = rows.FirstOrDefault();
        if (r is null)
        {
            // Not updated: either someone else changed it (409) or it is gone / hidden by RLS (404).
            var exists = await tx.QueryAsync<int>($"SELECT 1 FROM {d.Table} WHERE {d.Pk} = @id", new { id }).ConfigureAwait(false);
            return exists.Count > 0 ? UpdateResult.Conflict : UpdateResult.NotFound;
        }
        Dictionary<string, object?> Pick(string prefix) => d.AuditFields.ToDictionary(f => f, f => r[$"{prefix}_{f}"], StringComparer.Ordinal);
        Dictionary<int, string>? stageNames = null;
        if (d.AuditFields.Contains("StageId"))
        {
            stageNames = (await tx.QueryAsync<RefRow>("SELECT StageId AS Id, Name AS Label FROM crm.PipelineStages").ConfigureAwait(false)).ToDictionary(s => s.Id, s => s.Label);
        }
        var audit = Audit.Update(d, id, Pick("o"), Pick("n"), r.Int("n_OwnerId"), r.IntOrNull("n_TeamId"),
            (f, v) => f == "StageId" && v is int sid ? stageNames?.GetValueOrDefault(sid) ?? v : v);
        await WriteAuditAsync(audit, meta).ConfigureAwait(false);
        return UpdateResult.Ok;
    }

    public async Task<bool> RemoveAsync(string entity, int id, Meta meta)
    {
        var d = EntityDefinitions.All[entity];
        // No cascades: unlink the rows we can see first. Rows we cannot see, or opportunities, make the DELETE fail with 547.
        foreach (var stmt in CrmSql.Entities[entity].ClearLinks)
        {
            await tx.ExecuteAsync(stmt, new { id }).ConfigureAwait(false);
        }
        var rows = await tx.QueryRowsAsync($"DELETE {d.Table} OUTPUT deleted.OwnerId, deleted.TeamId, deleted.{d.LabelCol} AS Label WHERE {d.Pk} = @id", new { id }).ConfigureAwait(false);
        var r = rows.FirstOrDefault();
        if (r is null)
        {
            return false;
        }
        await WriteAuditAsync([Audit.Delete(d, id, r.S("Label"), r.Int("OwnerId"), r.IntOrNull("TeamId"))], meta).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SetDoneAsync(int id, bool done, Meta meta)
    {
        var p = P();
        p.Add("d", done, DbType.Boolean);
        p.Add("by", meta.UserId, DbType.Int32);
        p.Add("id", id, DbType.Int32);
        var rows = await tx.QueryAsync<int>(
            """
            UPDATE crm.Activities SET IsDone = @d, DoneAt = CASE WHEN @d = 1 THEN SYSUTCDATETIME() END, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME()
              OUTPUT inserted.ActivityId WHERE ActivityId = @id
            """, p).ConfigureAwait(false);
        return rows.Count > 0;
    }

    // ---- Aggregates for KPIs, charts and the account list (RLS-filtered; amounts stay in their own currency)

    private static readonly IReadOnlyDictionary<string, (string Select, string Group, string? Join)> Dim = new Dictionary<string, (string, string, string?)>(StringComparer.Ordinal)
    {
        ["stage"] = ("e.StageId AS StageId", "e.StageId", null),
        ["owner"] = ("e.OwnerId AS OwnerId, uo.DisplayName AS OwnerName", "e.OwnerId, uo.DisplayName", "JOIN crm.Users uo ON uo.UserId = e.OwnerId"),
        ["leadSource"] = ("ls.Label AS LeadSource", "ls.Label", "LEFT JOIN crm.PicklistValues ls ON ls.ValueId = e.LeadSourceValueId"),
        ["month"] = ("CONVERT(char(7), e.CloseDate, 23) AS Month", "CONVERT(char(7), e.CloseDate, 23)", null),
        ["account"] = ("e.AccountId AS AccountId", "e.AccountId", null),
    };

    /// <summary>Grouped opportunity sums/counts. Dimensions and stage types come from fixed sets; values are parameters.</summary>
    public async Task<IReadOnlyList<AggRow>> OppAggregatesAsync(OppAggFilter f)
    {
        var dims = (f.GroupBy ?? []).Distinct().Where(Dim.ContainsKey).Select(d => Dim[d]).ToList();
        var types = f.Types.Distinct().Where(t => t is "open" or "won" or "lost").ToList();
        if (types.Count == 0 || (f.AccountIds is { Count: 0 }))
        {
            return [];
        }
        var p = P();
        var where = new List<string> { $"s.StageType IN ({string.Join(", ", types.Select(t => $"'{t}'"))})" };
        if (f.From is not null) { where.Add("e.CloseDate >= @from"); p.Add("from", f.From, DbType.AnsiString, size: 10); }
        if (f.To is not null) { where.Add("e.CloseDate <= @to"); p.Add("to", f.To, DbType.AnsiString, size: 10); }
        if (f.OwnerId is { } o) { where.Add("e.OwnerId = @owner"); p.Add("owner", o, DbType.Int32); }
        if (f.TeamId is { } t2) { where.Add("e.TeamId = @team"); p.Add("team", t2, DbType.Int32); }
        if (f.AccountIds is not null)
        {
            where.Add("e.AccountId IN (SELECT CAST([value] AS int) FROM OPENJSON(@acc))");
            p.Add("acc", System.Text.Json.JsonSerializer.Serialize(f.AccountIds), DbType.String, size: -1);
        }
        var rows = await tx.QueryRowsAsync(
            $"""
            SELECT s.StageType, e.CurrencyCode{string.Concat(dims.Select(d => ", " + d.Select))}, COUNT(*) AS Cnt, SUM(e.Amount) AS Amount
              FROM crm.Opportunities e JOIN crm.PipelineStages s ON s.StageId = e.StageId {string.Join(" ", dims.Select(d => d.Join ?? ""))}
              WHERE {string.Join(" AND ", where)} GROUP BY s.StageType, e.CurrencyCode{string.Concat(dims.Select(d => ", " + d.Group))}
            """, p).ConfigureAwait(false);
        return rows.Select(r => new AggRow(r.S("StageType"), r.IntOrNull("StageId"), r.IntOrNull("OwnerId"), r["OwnerName"] as string, r["LeadSource"] as string, r["Month"] as string, r.IntOrNull("AccountId"), r.S("CurrencyCode").Trim(), r.Int("Cnt"), r.Dec("Amount"))).ToList();
    }

    private sealed record FxRaw(string CurrencyCode, string Month, decimal Rate);

    public async Task<IReadOnlyList<FxRateRow>> FxRatesAsync(string baseCurrency)
    {
        var p = P();
        p.Add("b", baseCurrency, DbType.AnsiStringFixedLength, size: 3);
        var rows = await tx.QueryAsync<FxRaw>("SELECT CurrencyCode, CONVERT(char(7), RateMonth, 23) AS Month, Rate FROM crm.FxRates WHERE BaseCurrency = @b", p).ConfigureAwait(false);
        return rows.Select(r => new FxRateRow(r.CurrencyCode.Trim(), r.Month, r.Rate)).ToList();
    }

    private sealed record TeamRow(int Id, string Name);

    /// <summary>Teams for the Analytics filter (team names are not sensitive; crm.Teams has no FILTER).</summary>
    public async Task<IReadOnlyList<(int Id, string Name)>> TeamsAsync() =>
        (await tx.QueryAsync<TeamRow>("SELECT TeamId AS Id, Name FROM crm.Teams WHERE IsActive = 1 ORDER BY Name").ConfigureAwait(false)).Select(t => (t.Id, t.Name)).ToList();

    private sealed record TypeCount(string ActivityType, int Cnt);

    public async Task<IReadOnlyList<(string Type, int Count)>> ActivitiesByTypeAsync(DateRange range, int? ownerId, int? teamId)
    {
        var p = P();
        p.Add("from", range.From, DbType.AnsiString, size: 10);
        p.Add("to", range.To, DbType.AnsiString, size: 10);
        var extra = "";
        if (ownerId is { } o) { extra += " AND OwnerId = @o"; p.Add("o", o, DbType.Int32); }
        if (teamId is { } t) { extra += " AND TeamId = @t"; p.Add("t", t, DbType.Int32); }
        var rows = await tx.QueryAsync<TypeCount>(
            $"SELECT ActivityType, COUNT(*) AS Cnt FROM crm.Activities WHERE COALESCE(DueDate, CAST(CreatedAt AS date)) BETWEEN @from AND @to{extra} GROUP BY ActivityType", p).ConfigureAwait(false);
        return rows.Select(r => (r.ActivityType, r.Cnt)).ToList();
    }
}
