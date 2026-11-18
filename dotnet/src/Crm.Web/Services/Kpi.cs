using System.Globalization;
using Crm.Web.Domain;

namespace Crm.Web.Services;

public sealed record Tile(string Key, string Label, decimal? Value, string Kind, string Context, IReadOnlyList<string> Notes, IReadOnlyList<string> ExcludedCurrencies, string Href);

/// <summary>KPI tiles (port of kpi.ts). Win rate = won / (won + lost); null when nothing closed (never a division by zero).</summary>
public static class Kpi
{
    public const int WinRateDays = 90;

    public static DateRange WinRateRange(string today) => new(Periods.AddDays(today, -(WinRateDays - 1)), today);

    public static (Converted Total, Converted Weighted, int Count) PipelineTotals(IReadOnlyList<AggRow> open, IReadOnlyList<Stage> stages, Converter conv, string currentMonth)
    {
        var prob = stages.ToDictionary(s => s.Id, s => s.Probability);
        (string, int, decimal) Sel(AggRow r) => (r.Currency, r.Count, r.Amount);
        var total = conv.ConvertRows(open, Sel, _ => currentMonth);
        var weighted = conv.ConvertRows(open, Sel, _ => currentMonth, r => (prob.GetValueOrDefault(r.StageId ?? -1)) / 100m);
        return (total, weighted, open.Sum(r => r.Count));
    }

    public static (int Won, int Lost, decimal? Rate) WinRate(IEnumerable<AggRow> rows)
    {
        var list = rows.ToList();
        var won = list.Where(r => r.StageType == "won").Sum(r => r.Count);
        var lost = list.Where(r => r.StageType == "lost").Sum(r => r.Count);
        return (won, lost, won + lost == 0 ? null : (decimal)won / (won + lost));
    }

    private static IReadOnlyList<string> NotesOf(Converted c) => new[] { Converter.ExclusionNote(c), Converter.FallbackNote(c) }.Where(s => s.Length > 0).ToList();

    private static IReadOnlyList<string> MissingOf(Converted c) => c.Excluded.Select(e => e.Currency).ToList();

    private static string DealWord(int n) => $"{n.ToString(CultureInfo.InvariantCulture)} {(n == 1 ? "deal" : "deals")}";

    public static IReadOnlyList<Tile> ComputeTiles(string today, int fiscalStartMonth, IReadOnlyList<Stage> stages, Converter conv, IReadOnlyList<AggRow> openRows, IReadOnlyList<AggRow> quarterWonRows, IReadOnlyList<AggRow> winRateRows)
    {
        var curMonth = Periods.MonthOf(today);
        var month = Periods.MonthRange(today);
        var quarter = Periods.QuarterRange(today, fiscalStartMonth);
        var pipe = PipelineTotals(openRows, stages, conv, curMonth);
        var wonQuarterRows = quarterWonRows.Where(r => r.StageType == "won").ToList();
        var wonMonthRows = wonQuarterRows.Where(r => r.Month == curMonth).ToList();
        string ByCloseMonth(AggRow r) => r.Month ?? curMonth;
        (string, int, decimal) Sel(AggRow r) => (r.Currency, r.Count, r.Amount);
        var wonMonth = conv.ConvertRows(wonMonthRows, Sel, ByCloseMonth);
        var wonQuarter = conv.ConvertRows(wonQuarterRows, Sel, ByCloseMonth);
        var wr = WinRate(winRateRows);
        var win = WinRateRange(today);
        return
        [
            new("open", "Open pipeline", pipe.Total.Total, "money", DealWord(pipe.Count).Replace("deal", "opp", StringComparison.Ordinal), NotesOf(pipe.Total), MissingOf(pipe.Total), "/opportunities?status=open"),
            new("weighted", "Weighted pipeline", pipe.Weighted.Total, "money", "amount × stage probability", NotesOf(pipe.Weighted), MissingOf(pipe.Weighted), "/opportunities?status=open"),
            new("won-month", "Won this month", wonMonth.Total, "money", DealWord(wonMonthRows.Sum(r => r.Count)), NotesOf(wonMonth), MissingOf(wonMonth), $"/opportunities?status=won&from={month.From}&to={month.To}"),
            new("won-quarter", "Won this quarter", wonQuarter.Total, "money", DealWord(wonQuarterRows.Sum(r => r.Count)), NotesOf(wonQuarter), MissingOf(wonQuarter), $"/opportunities?status=won&from={quarter.From}&to={quarter.To}"),
            new("win-rate", "Win rate (last 90 d)", wr.Rate is null ? null : wr.Rate * 100, "percent", wr.Rate is null ? "No closed deals yet" : $"{wr.Won} of {wr.Won + wr.Lost}", [], [], $"/opportunities?status=closed&from={win.From}&to={win.To}"),
        ];
    }
}
