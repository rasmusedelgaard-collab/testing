using System.Text.RegularExpressions;
using Crm.Web.Services;

namespace Crm.Web.Domain;

public enum ColType { Text, Int, Date, Time, Money, Bool }

public sealed record ColumnSpec(string Name, ColType Type, int Length = 0);

public sealed record SortSpec(string Sql, string Field);

public enum FilterKind { Int, Date, Enum }

public sealed record FilterSpec(string Key, FilterKind Kind, string[]? Values = null, string? Default = null);

/// <summary>Port of domain/entities.ts. Sort/search/filter fragments are whitelists: user input only ever selects a key.</summary>
public sealed record EntityDef(
    string Key,
    string Table,
    string Pk,
    string Path,
    string Singular,
    string Plural,
    string AuditType,
    string LabelCol,
    ColumnSpec[] Columns,
    string[] AuditFields,
    IReadOnlyDictionary<string, SortSpec> Sorts,
    string DefaultSort,
    string DefaultDir,
    SortSpec[] Search,
    FilterSpec[] Filters);

public static partial class EntityDefinitions
{
    public const int MaxInt = 2_147_483_647;
    public static readonly int[] PageSizes = [25, 50, 100];

    private static ColumnSpec Text(string n, int len) => new(n, ColType.Text, len);

    private static ColumnSpec I(string n) => new(n, ColType.Int);

    private static IReadOnlyDictionary<string, SortSpec> Sort(params (string, string, string)[] s) =>
        s.ToDictionary(x => x.Item1, x => new SortSpec(x.Item2, x.Item3), StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, EntityDef> All = new Dictionary<string, EntityDef>(StringComparer.Ordinal)
    {
        ["accounts"] = new("accounts", "crm.Accounts", "AccountId", "/accounts", "account", "Accounts", "account", "Name",
            [Text("Name", 120), Text("Website", 255), Text("Phone", 40), I("IndustryValueId"), Text("Street", 200), Text("PostalCode", 20), Text("City", 100), Text("CountryCode", 2), Text("Notes", 2000)],
            ["OwnerId"],
            Sort(("name", "e.Name", "Name"), ("industry", "iv.Label", "IndustryLabel"), ("city", "e.City", "City"), ("owner", "uo.DisplayName", "OwnerName")),
            "name", "asc",
            [new("e.Name", "Name"), new("e.City", "City")],
            [new("owner", FilterKind.Int), new("industry", FilterKind.Int)]),
        ["contacts"] = new("contacts", "crm.Contacts", "ContactId", "/contacts", "contact", "Contacts", "contact", "LastName",
            [I("AccountId"), Text("FirstName", 80), Text("LastName", 80), Text("JobTitle", 120), Text("Email", 254), Text("Phone", 40), Text("Mobile", 40)],
            ["OwnerId"],
            Sort(("name", "e.LastName", "LastName"), ("account", "a.Name", "AccountName"), ("title", "e.JobTitle", "JobTitle"), ("email", "e.Email", "Email"), ("owner", "uo.DisplayName", "OwnerName")),
            "name", "asc",
            [new("e.FirstName", "FirstName"), new("e.LastName", "LastName"), new("e.Email", "Email"), new("a.Name", "AccountName")],
            [new("account", FilterKind.Int), new("owner", FilterKind.Int)]),
        ["opportunities"] = new("opportunities", "crm.Opportunities", "OpportunityId", "/opportunities", "opportunity", "Opportunities", "opportunity", "Name",
            [Text("Name", 120), I("AccountId"), I("PrimaryContactId"), I("StageId"), new("Amount", ColType.Money), Text("CurrencyCode", 3), new("CloseDate", ColType.Date), I("LeadSourceValueId"), I("LostReasonValueId")],
            ["Amount", "StageId", "CurrencyCode", "CloseDate", "OwnerId"],
            Sort(("name", "e.Name", "Name"), ("account", "a.Name", "AccountName"), ("stage", "s.SortOrder", "StageSort"), ("amount", "e.Amount", "Amount"), ("close", "e.CloseDate", "CloseDate"), ("owner", "uo.DisplayName", "OwnerName")),
            "close", "asc",
            [new("e.Name", "Name"), new("a.Name", "AccountName")],
            [new("stage", FilterKind.Int), new("owner", FilterKind.Int), new("from", FilterKind.Date), new("to", FilterKind.Date), new("status", FilterKind.Enum, ["open", "won", "lost", "closed", "all"], "open"), new("account", FilterKind.Int), new("contact", FilterKind.Int)]),
        ["activities"] = new("activities", "crm.Activities", "ActivityId", "/activities", "activity", "Activities", "activity", "Subject",
            [Text("ActivityType", 10), Text("Subject", 160), Text("Description", 4000), I("AccountId"), I("ContactId"), I("OpportunityId"), new("DueDate", ColType.Date), new("DueTime", ColType.Time), new("IsDone", ColType.Bool)],
            ["OwnerId"],
            Sort(("type", "e.ActivityType", "ActivityType"), ("subject", "e.Subject", "Subject"), ("due", "e.DueDate", "DueDate"), ("owner", "uo.DisplayName", "OwnerName")),
            "due", "asc",
            [new("e.Subject", "Subject")],
            [new("type", FilterKind.Enum, ["call", "meeting", "task", "note", "actionable"]), new("status", FilterKind.Enum, ["open", "done", "all"], "open"), new("due", FilterKind.Enum, ["overdue", "today", "week", "due", "any"], "any"), new("owner", FilterKind.Int), new("account", FilterKind.Int), new("contact", FilterKind.Int), new("opportunity", FilterKind.Int)]),
    };

    public static IEnumerable<EntityDef> List => All.Values;

    public static EntityDef? Find(string? key) => key is not null && All.TryGetValue(key, out var d) ? d : null;

    [GeneratedRegex(@"^[1-9][0-9]{0,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PositiveInt();

    public static bool TryParseId(string? raw, out int id)
    {
        id = 0;
        if (raw is null || !PositiveInt().IsMatch(raw) || long.Parse(raw, System.Globalization.CultureInfo.InvariantCulture) > MaxInt)
        {
            return false;
        }
        id = int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Parses the URL query into a safe ListQuery: unknown sort keys, bad numbers and bad enum values fall back to defaults.</summary>
    public static ListQuery ParseListQuery(EntityDef def, Func<string, string?> query, string today, int maxPageSize = 100)
    {
        string Str(string k) => query(k) ?? "";
        var sort = def.Sorts.ContainsKey(Str("sort")) ? Str("sort") : def.DefaultSort;
        var dir = Str("dir") == "desc" ? "desc" : Str("dir") == "asc" ? "asc" : def.DefaultDir;
        var pageSize = PositiveInt().IsMatch(Str("pageSize")) && int.TryParse(Str("pageSize"), out var ps) && PageSizes.Contains(ps) && ps <= maxPageSize ? ps : 25;
        var page = PositiveInt().IsMatch(Str("page")) && int.TryParse(Str("page"), out var pg) && pg <= 100000 ? pg : 1;
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in def.Filters)
        {
            var v = Str(f.Key).Trim();
            if (f.Kind == FilterKind.Int && PositiveInt().IsMatch(v) && long.Parse(v, System.Globalization.CultureInfo.InvariantCulture) <= MaxInt)
            {
                filters[f.Key] = v;
            }
            else if (f.Kind == FilterKind.Date && NumberParser.IsRealDate(v))
            {
                filters[f.Key] = v;
            }
            else if (f.Kind == FilterKind.Enum)
            {
                if (f.Values?.Contains(v) == true)
                {
                    filters[f.Key] = v;
                }
                else if (f.Default is not null)
                {
                    filters[f.Key] = f.Default;
                }
            }
        }
        var q = Str("q").Trim();
        return new ListQuery(q.Length > 100 ? q[..100] : q, page, pageSize, sort, dir, filters, today);
    }
}
