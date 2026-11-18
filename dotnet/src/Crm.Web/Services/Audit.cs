using System.Globalization;
using Crm.Web.Domain;

namespace Crm.Web.Services;

public sealed record AuditRow(string EntityType, int EntityId, string Action, string? FieldName, string? OldValue, string? NewValue, int OwnerId, int? TeamId);

/// <summary>Audit row builders (port of services/audit.ts). Values are invariant text, truncated to 4000.</summary>
public static class Audit
{
    private const int Max = 4000;

    /// <summary>Invariant text: '.' decimals (trailing zeros trimmed, as the Node app wrote them), ISO dates, booleans 0/1.</summary>
    public static string? Text(object? v)
    {
        var s = v switch
        {
            null => null,
            bool b => b ? "1" : "0",
            decimal d => d.ToString("0.############################", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString(),
        };
        return s is { Length: > Max } ? s[..Max] : s;
    }

    public static AuditRow Create(EntityDef def, int id, string label, int ownerId, int? teamId) =>
        new(def.AuditType, id, "C", null, null, Text(label), ownerId, teamId);

    /// <summary>Owner and team are those BEFORE the delete.</summary>
    public static AuditRow Delete(EntityDef def, int id, string label, int ownerId, int? teamId) =>
        new(def.AuditType, id, "D", null, Text(label), null, ownerId, teamId);

    /// <summary>One row per changed audited field. Old/new come from UPDATE ... OUTPUT deleted/inserted.</summary>
    public static IReadOnlyList<AuditRow> Update(EntityDef def, int id, IReadOnlyDictionary<string, object?> oldVals, IReadOnlyDictionary<string, object?> newVals, int ownerId, int? teamId, Func<string, object?, object?>? label = null)
    {
        label ??= (_, v) => v;
        var rows = new List<AuditRow>();
        foreach (var field in def.AuditFields)
        {
            var o = Text(label(field, oldVals.GetValueOrDefault(field)));
            var n = Text(label(field, newVals.GetValueOrDefault(field)));
            if (o != n)
            {
                rows.Add(new AuditRow(def.AuditType, id, "U", field, o, n, ownerId, teamId));
            }
        }
        return rows;
    }
}
