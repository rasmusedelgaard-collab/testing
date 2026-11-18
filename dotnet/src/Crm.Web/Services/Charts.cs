using Crm.Web.Domain;

namespace Crm.Web.Services;

public sealed record ChartTableRow(IReadOnlyList<string> Cells, string? Flag = null);

public sealed record ChartVm(string Id, string Title, string Kind, string ValueKind, string SeriesLabel, string Takeaway, IReadOnlyList<string> Notes,
    IReadOnlyList<string> Labels, IReadOnlyList<decimal> Data, IReadOnlyList<string> TableColumns, IReadOnlyList<ChartTableRow> TableRows, string AriaLabel, bool Empty);

public sealed record FormatCtx(Formatter F, string Locale, string Base);

/// <summary>The five Analytics charts as view models (port of charts.ts). Each has its data-table fallback.</summary>
public static class Charts
{
    public const string EmptyText = "No data for this period. Try a longer date range.";

    private static string Amount(Converted c, FormatCtx x) => c.Excluded.Count > 0 && c.Total == 0 ? "–" : x.F.Money(c.Total, x.Base);

    private static string? FlagOf(Converted c) =>
        new[] { c.Excluded.Count > 0 ? "Not included: no FX rate" : "", c.FallbackCurrencies.Count > 0 ? "Latest rate used" : "" }.Where(s => s.Length > 0) is var parts && parts.Any() ? string.Join("; ", parts) : null;

    private static ChartVm Empty(string id, string title, string kind, string valueKind, string seriesLabel) =>
        new(id, title, kind, valueKind, seriesLabel, EmptyText, [], [], [], [], [], $"{title}: no data", true);

    private static string Aria(string what, string takeaway) => $"{what}. {takeaway.TrimEnd('.')}.";

    private static (string, int, decimal) Sel(AggRow r) => (r.Currency, r.Count, r.Amount);

    public static ChartVm PipelineByStage(IReadOnlyList<AggRow> rows, IReadOnlyList<Stage> stages, Converter conv, string currentMonth, FormatCtx c)
    {
        var open = stages.Where(s => s.Type == "open").OrderBy(s => s.SortOrder).ToList();
        var per = open.Select(s => (S: s, Conv: conv.ConvertRows(rows.Where(r => r.StageId == s.Id), Sel, _ => currentMonth), N: rows.Where(r => r.StageId == s.Id).Sum(r => r.Count))).ToList();
        var all = conv.ConvertRows(rows, Sel, _ => currentMonth);
        var notes = new[] { Converter.ExclusionNote(all), Converter.FallbackNote(all) }.Where(s => s.Length > 0).ToList();
        var cols = new[] { "Stage", "Opportunities", $"Amount ({c.Base})" };
        var tableRows = per.Select(p => new ChartTableRow([p.S.Name, p.N.ToString(System.Globalization.CultureInfo.InvariantCulture), Amount(p.Conv, c)], FlagOf(p.Conv))).ToList();
        var vm = new ChartVm("pipeline", "Pipeline by stage", "bar", "money", $"Amount in {c.Base}", EmptyText, notes, per.Select(p => p.S.Name).ToList(), per.Select(p => p.Conv.Total).ToList(), cols, tableRows, "Pipeline by stage: no data", true);
        if (rows.Count == 0)
        {
            return vm;
        }
        var top = per.OrderByDescending(p => p.Conv.Total).First();
        var take = $"{top.S.Name} holds the most value: {NumberFormats.CompactMoney(top.Conv.Total, c.Base, c.Locale)}";
        return vm with { Empty = false, Takeaway = take, AriaLabel = Aria("Bar chart of open pipeline by stage", take) };
    }

    public static ChartVm WonRevenueByMonth(IReadOnlyList<AggRow> rows, DateRange range, Converter conv, FormatCtx c)
    {
        var months = Periods.MonthsBetween(range.From, range.To);
        var per = months.Select(m =>
        {
            var mr = rows.Where(r => r.Month == m).ToList();
            return (M: m, Conv: conv.ConvertRows(mr, Sel, _ => m), N: mr.Sum(r => r.Count));
        }).ToList();
        var all = conv.ConvertRows(rows, Sel, r => r.Month ?? Periods.MonthOf(range.To));
        var notes = new[] { Converter.ExclusionNote(all), Converter.FallbackNote(all) }.Where(s => s.Length > 0).ToList();
        var cols = new[] { "Month", "Deals", $"Revenue ({c.Base})" };
        var tableRows = per.Select(p => new ChartTableRow([NumberFormats.MonthLabel(p.M, c.Locale), p.N.ToString(System.Globalization.CultureInfo.InvariantCulture), Amount(p.Conv, c)], FlagOf(p.Conv))).ToList();
        var vm = new ChartVm("won", "Won revenue by month", "line", "money", $"Won revenue in {c.Base}", EmptyText, notes, months.Select(m => NumberFormats.MonthLabel(m, c.Locale)).ToList(), per.Select(p => p.Conv.Total).ToList(), cols, tableRows, "Won revenue by month: no data", true);
        if (rows.Count == 0)
        {
            return vm;
        }
        var best = per.OrderByDescending(p => p.Conv.Total).First();
        var take = $"Best month: {NumberFormats.MonthLabel(best.M, c.Locale)} with {NumberFormats.CompactMoney(best.Conv.Total, c.Base, c.Locale)}";
        return vm with { Empty = false, Takeaway = take, AriaLabel = Aria("Line chart of won revenue by month", take) };
    }

    public static ChartVm WinRateByOwner(IReadOnlyList<AggRow> rows, FormatCtx c)
    {
        var owners = new Dictionary<int, (string Name, int Won, int Lost)>();
        foreach (var r in rows.Where(r => r.OwnerId is not null && r.StageType != "open"))
        {
            (string Name, int Won, int Lost) o = owners.TryGetValue(r.OwnerId!.Value, out var prev) ? prev : (r.OwnerName ?? "Unknown", 0, 0);
            owners[r.OwnerId.Value] = r.StageType == "won" ? (o.Name, o.Won + r.Count, o.Lost) : (o.Name, o.Won, o.Lost + r.Count);
        }
        var list = owners.Values.Select(o => (o.Name, o.Won, o.Lost, Rate: (decimal)o.Won / (o.Won + o.Lost))).OrderByDescending(o => o.Rate).ThenBy(o => o.Name, StringComparer.Ordinal).ToList();
        var cols = new[] { "Owner", "Won", "Lost", "Win rate" };
        var tableRows = list.Select(o => new ChartTableRow([o.Name, o.Won.ToString(System.Globalization.CultureInfo.InvariantCulture), o.Lost.ToString(System.Globalization.CultureInfo.InvariantCulture), NumberFormats.Percent(o.Rate, c.Locale, 1)])).ToList();
        var vm = new ChartVm("winrate", "Win rate by owner", "hbar", "percent", "Win rate", EmptyText, [], list.Select(o => o.Name).ToList(), list.Select(o => Math.Round(o.Rate * 1000, MidpointRounding.AwayFromZero) / 10).ToList(), cols, tableRows, "Win rate by owner: no data", true);
        if (list.Count == 0)
        {
            return vm;
        }
        var take = $"{list[0].Name} has the highest win rate: {NumberFormats.Percent(list[0].Rate, c.Locale)}";
        return vm with { Empty = false, Takeaway = take, AriaLabel = Aria("Horizontal bar chart of win rate by owner", take) };
    }

    private static readonly string[] TypeOrder = ["call", "meeting", "task", "note"];
    private static readonly IReadOnlyDictionary<string, string> TypeLabel = new Dictionary<string, string> { ["call"] = "Call", ["meeting"] = "Meeting", ["task"] = "Task", ["note"] = "Note" };

    public static ChartVm ActivitiesByType(IReadOnlyList<(string Type, int Count)> rows, FormatCtx c)
    {
        var list = TypeOrder.Select(t => (T: t, N: rows.FirstOrDefault(r => r.Type == t).Count)).ToList();
        var cols = new[] { "Type", "Activities" };
        var tableRows = list.Select(x => new ChartTableRow([TypeLabel[x.T], NumberFormats.Count(x.N, c.Locale)])).ToList();
        var vm = new ChartVm("activities", "Activities by type", "bar", "count", "Activities", EmptyText, [], list.Select(x => TypeLabel[x.T]).ToList(), list.Select(x => (decimal)x.N).ToList(), cols, tableRows, "Activities by type: no data", true);
        var total = list.Sum(x => x.N);
        if (total == 0)
        {
            return vm;
        }
        var top = list.OrderByDescending(x => x.N).First();
        var take = $"{TypeLabel[top.T]} is the most common type: {top.N} of {total}";
        return vm with { Empty = false, Takeaway = take, AriaLabel = Aria("Bar chart of activities by type", take) };
    }

    public static ChartVm OppsByLeadSource(IReadOnlyList<AggRow> rows, FormatCtx c)
    {
        var list = rows.GroupBy(r => r.LeadSource ?? "(none)").Select(g => (Name: g.Key, N: g.Sum(r => r.Count))).OrderByDescending(x => x.N).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        var cols = new[] { "Lead source", "Opportunities" };
        var tableRows = list.Select(x => new ChartTableRow([x.Name, NumberFormats.Count(x.N, c.Locale)])).ToList();
        var vm = new ChartVm("leadsource", "Opportunities by lead source", "hbar", "count", "Opportunities", EmptyText, [], list.Select(x => x.Name).ToList(), list.Select(x => (decimal)x.N).ToList(), cols, tableRows, "Opportunities by lead source: no data", true);
        if (list.Count == 0)
        {
            return vm;
        }
        var take = $"{list[0].Name} brings the most opportunities: {list[0].N}";
        return vm with { Empty = false, Takeaway = take, AriaLabel = Aria("Horizontal bar chart of opportunities by lead source", take) };
    }
}
