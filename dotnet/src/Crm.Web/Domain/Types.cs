using System.Globalization;

namespace Crm.Web.Domain;

public sealed record Settings(
    string AppName,
    string LocaleCode,
    string Timezone,
    string BaseCurrency,
    IReadOnlyList<string> EnabledCurrencies,
    string VisibilityMode,
    string AccentColor,
    int FirstDayOfWeek,
    int FiscalStartMonth);

public sealed record Stage(int Id, string Name, int SortOrder, int Probability, string Type);

public sealed record PicklistValue(int Id, string? Code, string Label, bool Active);

public sealed record Lookups(Settings Settings, IReadOnlyList<Stage> Stages, IReadOnlyDictionary<string, IReadOnlyList<PicklistValue>> Picklists)
{
    public IReadOnlyList<PicklistValue> List(string code) => Picklists.TryGetValue(code, out var l) ? l : [];
}

public sealed record OwnerOption(int Id, string Name, string? TeamName, bool Active);

public sealed record RefOption(int Id, string Label);

public sealed record ListQuery(string Q, int Page, int PageSize, string Sort, string Dir, IReadOnlyDictionary<string, string> Filters, string Today)
{
    public string Filter(string key) => Filters.TryGetValue(key, out var v) ? v : "";
}

public sealed record HistoryRow(string At, string By, string Action, string? Field, string? OldValue, string? NewValue);

public sealed record Meta(int UserId, Guid CorrelationId);

public enum UpdateResult { Ok, Conflict, NotFound }

/// <summary>A record as the UI sees it: raw columns plus joined display names. Dates are yyyy-MM-dd, timestamps ISO strings.</summary>
public sealed class Row(IDictionary<string, object?> data)
{
    private readonly IDictionary<string, object?> _d = data;

    public object? this[string key] => _d.TryGetValue(key, out var v) ? v : null;

    public bool Has(string key) => _d.ContainsKey(key);

    public string S(string key) => this[key] switch { null => "", byte[] bytes => Convert.ToBase64String(bytes), bool b => b ? "1" : "0", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), var o => o.ToString() ?? "" };

    public int Int(string key) => this[key] is null ? 0 : Convert.ToInt32(this[key], CultureInfo.InvariantCulture);

    public int? IntOrNull(string key) => this[key] is null ? null : Convert.ToInt32(this[key], CultureInfo.InvariantCulture);

    public decimal Dec(string key) => this[key] is null ? 0m : Convert.ToDecimal(this[key], CultureInfo.InvariantCulture);

    public bool Bool(string key) => this[key] is not null && Convert.ToBoolean(this[key], CultureInfo.InvariantCulture);

    public void Set(string key, object? value) => _d[key] = value;

    public IEnumerable<string> Keys => _d.Keys;
}

public sealed record AggRow(string StageType, int? StageId, int? OwnerId, string? OwnerName, string? LeadSource, string? Month, int? AccountId, string Currency, int Count, decimal Amount);

public sealed record OppAggFilter(IReadOnlyList<string> Types, string? From = null, string? To = null, int? OwnerId = null, int? TeamId = null, IReadOnlyList<int>? AccountIds = null, IReadOnlyList<string>? GroupBy = null);
