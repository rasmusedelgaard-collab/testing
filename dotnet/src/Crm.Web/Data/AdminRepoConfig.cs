using System.Data;
using Crm.Web.Domain;
using Crm.Web.Services;
using Dapper;

namespace Crm.Web.Data;

public sealed record FxRateRec(int Id, string Currency, string Month, decimal Rate, string ByName, string At);

public sealed record FxSaveOutcome(bool Ok, int DuplicateOf = 0, int Id = 0);

public sealed record StageUsageRow(int Id, string Name, int SortOrder, int Probability, string Type, int Used);

public sealed record PicklistValueRow(int Id, string? Code, string Label, bool Active, int SortOrder);

public sealed record PicklistData(string Name, IReadOnlyList<PicklistValueRow> Values);

/// <summary>Currency usage, FX rates, pipeline stages and picklists (port of the matching parts of sqlAdmin.ts).</summary>
public sealed partial class AdminRepo
{
    private sealed record UsageRow(string CurrencyCode, int Cnt);

    public async Task<IReadOnlyDictionary<string, int>> CurrencyUsageAsync() =>
        (await tx.QueryAsync<UsageRow>("SELECT CurrencyCode, COUNT(*) AS Cnt FROM crm.Opportunities GROUP BY CurrencyCode").ConfigureAwait(false))
            .ToDictionary(r => r.CurrencyCode.Trim(), r => r.Cnt, StringComparer.Ordinal);

    // ---------------- FX rates (ADR-0005: 1 CurrencyCode = Rate BaseCurrency) ----------------

    private const string FxSelect = """
        SELECT f.FxRateId AS Id, f.CurrencyCode AS Currency, CONVERT(char(7), f.RateMonth, 23) AS Month, f.Rate, u.DisplayName AS ByName, CONVERT(varchar(23), f.ModifiedAt, 126) + 'Z' AS At
          FROM crm.FxRates f JOIN crm.Users u ON u.UserId = f.ModifiedBy
        """;

    private static FxRateRec Trim(FxRateRec r) => r with { Currency = r.Currency.Trim() };

    /// <summary>Rates for one base currency (rates of an earlier base stay in the table but are not listed or used).</summary>
    public async Task<IReadOnlyList<FxRateRec>> FxRatesAsync(string baseCurrency)
    {
        var p = P();
        p.Add("b", baseCurrency, DbType.AnsiStringFixedLength, size: 3);
        return (await tx.QueryAsync<FxRateRec>($"{FxSelect} WHERE f.BaseCurrency = @b ORDER BY f.RateMonth DESC, f.CurrencyCode", p).ConfigureAwait(false)).Select(Trim).ToList();
    }

    public async Task<FxRateRec?> FxRateAsync(int id)
    {
        var r = await tx.QuerySingleOrDefaultAsync<FxRateRec>($"{FxSelect} WHERE f.FxRateId = @id", new { id }).ConfigureAwait(false);
        return r is null ? null : Trim(r);
    }

    /// <summary>A duplicate (base, currency, month) is reported, never overwritten.</summary>
    public async Task<FxSaveOutcome> SaveFxRateAsync(int? id, string baseCurrency, string currency, string month, decimal rate, Meta meta)
    {
        var p = P();
        p.Add("b", baseCurrency, DbType.AnsiStringFixedLength, size: 3);
        p.Add("c", currency, DbType.AnsiStringFixedLength, size: 3);
        p.Add("m", $"{month}-01", DbType.AnsiString, size: 10);
        p.Add("id", id, DbType.Int32);
        var dup = await tx.QueryAsync<int>("SELECT FxRateId FROM crm.FxRates WHERE BaseCurrency = @b AND CurrencyCode = @c AND RateMonth = @m AND (@id IS NULL OR FxRateId <> @id)", p).ConfigureAwait(false);
        if (dup.Count > 0)
        {
            return new FxSaveOutcome(false, dup[0]);
        }
        p.Add("r", rate, DbType.Decimal, precision: 19, scale: 8);
        p.Add("by", meta.UserId, DbType.Int32);
        var rateText = Audit.Text(rate);
        if (id is null)
        {
            var newId = await tx.ExecuteScalarAsync<int>("INSERT crm.FxRates (BaseCurrency, CurrencyCode, RateMonth, Rate, ModifiedBy) OUTPUT inserted.FxRateId VALUES (@b, @c, @m, @r, @by)", p).ConfigureAwait(false);
            await AuditAsync("fxrate", newId, "C", null, null, $"{currency} {month} {rateText}", meta).ConfigureAwait(false);
            return new FxSaveOutcome(true, Id: newId);
        }
        var old = await FxRateAsync(id.Value).ConfigureAwait(false);
        if (old is null)
        {
            return new FxSaveOutcome(false);
        }
        await tx.ExecuteAsync("UPDATE crm.FxRates SET CurrencyCode = @c, RateMonth = @m, Rate = @r, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE FxRateId = @id", p).ConfigureAwait(false);
        if (old.Rate != rate) { await AuditAsync("fxrate", id.Value, "U", "Rate", Audit.Text(old.Rate), rateText, meta).ConfigureAwait(false); }
        if (old.Currency != currency) { await AuditAsync("fxrate", id.Value, "U", "CurrencyCode", old.Currency, currency, meta).ConfigureAwait(false); }
        if (old.Month != month) { await AuditAsync("fxrate", id.Value, "U", "RateMonth", old.Month, month, meta).ConfigureAwait(false); }
        return new FxSaveOutcome(true, Id: id.Value);
    }

    private sealed record DeletedFx(string CurrencyCode, string Month, decimal Rate);

    public async Task<bool> DeleteFxRateAsync(int id, Meta meta)
    {
        var r = await tx.QueryAsync<DeletedFx>("DELETE crm.FxRates OUTPUT deleted.CurrencyCode, CONVERT(char(7), deleted.RateMonth, 23) AS Month, deleted.Rate WHERE FxRateId = @id", new { id }).ConfigureAwait(false);
        if (r.Count == 0)
        {
            return false;
        }
        await AuditAsync("fxrate", id, "D", null, $"{r[0].CurrencyCode.Trim()} {r[0].Month} {Audit.Text(r[0].Rate)}", null, meta).ConfigureAwait(false);
        return true;
    }

    // ---------------- Pipeline stages ----------------

    public async Task<IReadOnlyList<StageUsageRow>> StageUsageAsync() =>
        // Opportunities are counted under RLS: the app can only count what the Admin may see (Admin sees all).
        await tx.QueryAsync<StageUsageRow>(
            """
            SELECT s.StageId AS Id, s.Name, CAST(s.SortOrder AS int) AS SortOrder, CAST(s.Probability AS int) AS Probability, s.StageType AS Type,
                   (SELECT COUNT(*) FROM crm.Opportunities o WHERE o.StageId = s.StageId) AS Used
              FROM crm.PipelineStages s ORDER BY s.SortOrder
            """).ConfigureAwait(false);

    public async Task<SaveOutcomeAdmin> SaveStagesAsync(IReadOnlyList<SettingsValidators.StageInput> rows, Meta meta)
    {
        var old = await StageUsageAsync().ConfigureAwait(false);
        var ids = rows.Where(r => r.Id is not null).Select(r => r.Id!.Value).ToList();
        if (ids.Distinct().Count() != ids.Count || ids.Any(id => old.All(o => o.Id != id)))
        {
            return new SaveOutcomeAdmin(false, "The stage list is out of date. Reload the page and try again.");
        }
        var keep = ids.ToHashSet();
        if (old.FirstOrDefault(o => !keep.Contains(o.Id) && o.Used > 0) is { } inUse)
        {
            return new SaveOutcomeAdmin(false, $"Move {inUse.Used} {(inUse.Used == 1 ? "opportunity" : "opportunities")} out of \"{inUse.Name}\" before deleting it.");
        }
        foreach (var st in old.Where(o => !keep.Contains(o.Id)))
        {
            await tx.ExecuteAsync("DELETE crm.PipelineStages WHERE StageId = @id", new { id = st.Id }).ConfigureAwait(false);
            await AuditAsync("stage", st.Id, "D", null, st.Name, null, meta).ConfigureAwait(false);
        }
        // Two phases so renames/swaps and moving the Won/Lost flag never collide with the unique indexes.
        await tx.ExecuteAsync("UPDATE crm.PipelineStages SET StageType = 'open', Name = CONCAT(N'~', CAST(StageId AS nvarchar(12)))").ConfigureAwait(false);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var p = P();
            p.Add("n", r.Name, DbType.String, size: 60);
            p.Add("o", i + 1, DbType.Int16);
            p.Add("pr", r.Probability, DbType.Byte);
            p.Add("t", r.Type, DbType.AnsiString, size: 4);
            p.Add("by", meta.UserId, DbType.Int32);
            if (r.Id is null)
            {
                var newId = await tx.ExecuteScalarAsync<int>("INSERT crm.PipelineStages (Name, SortOrder, Probability, StageType, ModifiedBy) OUTPUT inserted.StageId VALUES (@n, @o, @pr, @t, @by)", p).ConfigureAwait(false);
                await AuditAsync("stage", newId, "C", null, null, r.Name, meta).ConfigureAwait(false);
                continue;
            }
            p.Add("id", r.Id.Value, DbType.Int32);
            await tx.ExecuteAsync("UPDATE crm.PipelineStages SET Name = @n, SortOrder = @o, Probability = @pr, StageType = @t, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE StageId = @id", p).ConfigureAwait(false);
            var o = old.First(x => x.Id == r.Id);
            (string Field, string A, string B)[] changes = [("Name", o.Name, r.Name), ("Probability", Audit.Text(o.Probability)!, Audit.Text(r.Probability)!), ("StageType", o.Type, r.Type), ("SortOrder", Audit.Text(o.SortOrder)!, Audit.Text(i + 1)!)];
            foreach (var (field, a, b) in changes.Where(c => c.A != c.B))
            {
                await AuditAsync("stage", r.Id.Value, "U", field, a, b, meta).ConfigureAwait(false);
            }
        }
        return new SaveOutcomeAdmin(true);
    }

    // ---------------- Picklists ----------------

    public async Task<PicklistData?> PicklistAsync(string code)
    {
        var p = P();
        p.Add("c", code, DbType.AnsiString, size: 30);
        var name = await tx.QuerySingleOrDefaultAsync<string>("SELECT Name FROM crm.Picklists WHERE Code = @c", p).ConfigureAwait(false);
        if (name is null)
        {
            return null;
        }
        var values = await tx.QueryAsync<PicklistValueRow>(
            "SELECT v.ValueId AS Id, v.Code, v.Label, v.IsActive AS Active, CAST(v.SortOrder AS int) AS SortOrder FROM crm.PicklistValues v JOIN crm.Picklists l ON l.PicklistId = v.PicklistId WHERE l.Code = @c ORDER BY v.SortOrder, v.Label", p).ConfigureAwait(false);
        return new PicklistData(name, values);
    }

    private async Task<bool> DuplicateLabelAsync(string code, string label, int? exceptId)
    {
        var p = P();
        p.Add("c", code, DbType.AnsiString, size: 30);
        p.Add("l", label, DbType.String, size: 80);
        p.Add("id", exceptId, DbType.Int32);
        return (await tx.QueryAsync<int>("SELECT 1 FROM crm.PicklistValues v JOIN crm.Picklists p ON p.PicklistId = v.PicklistId WHERE p.Code = @c AND v.Label = @l AND (@id IS NULL OR v.ValueId <> @id)", p).ConfigureAwait(false)).Count > 0;
    }

    public async Task<SaveOutcomeAdmin> AddPicklistValueAsync(string code, string label, Meta meta)
    {
        if (code == "activity_type")
        {
            return new SaveOutcomeAdmin(false, "Values cannot be added to this list.");
        }
        var l = await PicklistAsync(code).ConfigureAwait(false);
        if (l is null)
        {
            return new SaveOutcomeAdmin(false, "List not found.");
        }
        if (await DuplicateLabelAsync(code, label, null).ConfigureAwait(false))
        {
            return new SaveOutcomeAdmin(false, $"\"{label}\" already exists in this list.", "label");
        }
        var p = P();
        p.Add("l", label, DbType.String, size: 80);
        p.Add("o", l.Values.Select(v => v.SortOrder).DefaultIfEmpty(0).Max() + 1, DbType.Int16);
        p.Add("by", meta.UserId, DbType.Int32);
        p.Add("c", code, DbType.AnsiString, size: 30);
        var id = await tx.ExecuteScalarAsync<int>("INSERT crm.PicklistValues (PicklistId, Label, SortOrder, ModifiedBy) OUTPUT inserted.ValueId SELECT PicklistId, @l, @o, @by FROM crm.Picklists WHERE Code = @c", p).ConfigureAwait(false);
        await AuditAsync("picklistvalue", id, "C", null, null, label, meta).ConfigureAwait(false);
        return new SaveOutcomeAdmin(true, Id: id);
    }

    public async Task<SaveOutcomeAdmin> RenamePicklistValueAsync(string code, int id, string label, Meta meta)
    {
        var cur = (await PicklistAsync(code).ConfigureAwait(false))?.Values.FirstOrDefault(v => v.Id == id);
        if (cur is null)
        {
            return new SaveOutcomeAdmin(false, "Value not found.");
        }
        if (await DuplicateLabelAsync(code, label, id).ConfigureAwait(false))
        {
            return new SaveOutcomeAdmin(false, $"\"{label}\" already exists in this list.", $"label{id}");
        }
        if (cur.Label != label)
        {
            var p = P();
            p.Add("l", label, DbType.String, size: 80);
            p.Add("by", meta.UserId, DbType.Int32);
            p.Add("id", id, DbType.Int32);
            await tx.ExecuteAsync("UPDATE crm.PicklistValues SET Label = @l, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE ValueId = @id", p).ConfigureAwait(false);
            await AuditAsync("picklistvalue", id, "U", "Label", cur.Label, label, meta).ConfigureAwait(false);
        }
        return new SaveOutcomeAdmin(true, Id: id);
    }

    public async Task<SaveOutcomeAdmin> SetPicklistActiveAsync(string code, int id, bool active, Meta meta)
    {
        var cur = (await PicklistAsync(code).ConfigureAwait(false))?.Values.FirstOrDefault(v => v.Id == id);
        if (cur is null)
        {
            return new SaveOutcomeAdmin(false, "Value not found.");
        }
        if (cur.Active != active)
        {
            var p = P();
            p.Add("a", active, DbType.Boolean);
            p.Add("by", meta.UserId, DbType.Int32);
            p.Add("id", id, DbType.Int32);
            await tx.ExecuteAsync("UPDATE crm.PicklistValues SET IsActive = @a, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE ValueId = @id", p).ConfigureAwait(false);
            await AuditAsync("picklistvalue", id, "U", "IsActive", active ? "0" : "1", active ? "1" : "0", meta).ConfigureAwait(false);
        }
        return new SaveOutcomeAdmin(true, Id: id);
    }

    public async Task<SaveOutcomeAdmin> MovePicklistValueAsync(string code, int id, bool up, Meta meta)
    {
        var vals = (await PicklistAsync(code).ConfigureAwait(false))?.Values;
        var i = vals is null ? -1 : vals.ToList().FindIndex(v => v.Id == id);
        if (vals is null || i < 0)
        {
            return new SaveOutcomeAdmin(false, "Value not found.");
        }
        var j = up ? i - 1 : i + 1;
        if (j < 0 || j >= vals.Count)
        {
            return new SaveOutcomeAdmin(true, Id: id);
        }
        // Renumber the whole list so equal SortOrders never make the order ambiguous.
        var order = vals.Select(v => v.Id).ToList();
        (order[i], order[j]) = (order[j], order[i]);
        for (var k = 0; k < order.Count; k++)
        {
            var p = P();
            p.Add("o", k + 1, DbType.Int16);
            p.Add("by", meta.UserId, DbType.Int32);
            p.Add("id", order[k], DbType.Int32);
            await tx.ExecuteAsync("UPDATE crm.PicklistValues SET SortOrder = @o, ModifiedBy = @by, ModifiedAt = SYSUTCDATETIME() WHERE ValueId = @id AND SortOrder <> @o", p).ConfigureAwait(false);
        }
        await AuditAsync("picklistvalue", id, "U", "SortOrder", Audit.Text(i + 1), Audit.Text(j + 1), meta).ConfigureAwait(false);
        return new SaveOutcomeAdmin(true, Id: id);
    }
}
