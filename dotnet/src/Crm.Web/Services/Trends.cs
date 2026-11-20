using System.Globalization;
using System.Text;
using Crm.Web.Domain;

namespace Crm.Web.Services;

/// <summary>direction is "up", "down" or "flat" (icon); Text is the visible sentence; Range names the compared date ranges (title and hidden text).</summary>
/// <summary>Text is the full sentence (screen readers, titles); Short is the pill text ("12%", "3 pts").</summary>
public sealed record TrendResult(string Direction, string Text, string Range, string Short = "");

/// <summary>Sparkline geometry for a 120 x 32 viewBox plus the sentence that carries the information as text.</summary>
public sealed record SparkResult(string Points, string LastX, string LastY, string Text);

/// <summary>Dashboard trends and sparkline (UI redesign B-6, D7). Pure: the caller supplies the aggregate rows.</summary>
public static class Trends
{
    /// <summary>Month-to-date and the same number of days of the previous month (clamped to that month's end).</summary>
    public static (DateRange Current, DateRange Previous) MonthToDate(string today)
    {
        var start = Periods.MonthStartPlus(today, 0);
        var prevStart = Periods.MonthStartPlus(today, -1);
        var elapsed = Periods.DaysBetween(start, today);
        return (new DateRange(start, today), Clamp(prevStart, elapsed, Periods.EndAfterMonths(prevStart, 1)));
    }

    /// <summary>Quarter-to-date and the same number of days of the previous quarter.</summary>
    public static (DateRange Current, DateRange Previous) QuarterToDate(string today, int fiscalStartMonth)
    {
        var q = Periods.QuarterRange(today, fiscalStartMonth);
        var prevStart = Periods.MonthStartPlus(q.From, -3);
        return (new DateRange(q.From, today), Clamp(prevStart, Periods.DaysBetween(q.From, today), Periods.EndAfterMonths(prevStart, 3)));
    }

    private static DateRange Clamp(string start, int elapsedDays, string periodEnd)
    {
        var end = Periods.AddDays(start, elapsedDays);
        return new DateRange(start, string.CompareOrdinal(end, periodEnd) > 0 ? periodEnd : end);
    }

    /// <summary>The 90 days before the win-rate window.</summary>
    public static DateRange PreviousWinWindow(string today)
    {
        var win = Kpi.WinRateRange(today);
        return new DateRange(Periods.AddDays(win.From, -Kpi.WinRateDays), Periods.AddDays(win.From, -1));
    }

    private static string RangeText(DateRange cur, DateRange prev, Func<string, string> date) =>
        $"{date(cur.From)}–{date(cur.To)} compared with {date(prev.From)}–{date(prev.To)}";

    /// <summary>"Up 12% vs same days last month". period is "month" or "quarter". Null when both periods are empty.</summary>
    public static TrendResult? CompareAmounts(decimal current, decimal previous, string period, string locale, DateRange curRange, DateRange prevRange, Func<string, string> date)
    {
        var label = $"same days last {period}";
        var range = RangeText(curRange, prevRange, date);
        if (previous == 0)
        {
            return current == 0 ? new TrendResult("flat", $"No change vs {label}", range, "0%") : new TrendResult("up", $"None in the {label}", range, "New");
        }
        if (current == previous)
        {
            return new TrendResult("flat", $"No change vs {label}", range, "0%");
        }
        var fraction = Math.Abs(current - previous) / previous;
        var digits = fraction * 100 < 1 ? 1 : 0;
        var pct = NumberFormats.Percent(fraction, locale, digits);
        return current > previous ? new TrendResult("up", $"Up {pct} vs {label}", range, pct) : new TrendResult("down", $"Down {pct} vs {label}", range, pct);
    }

    /// <summary>"Up 4 pts vs previous 90 days". Null when either window has no closed opportunities.</summary>
    public static TrendResult? ComparePoints(decimal? currentRate, decimal? previousRate, DateRange curRange, DateRange prevRange, Func<string, string> date)
    {
        if (currentRate is null || previousRate is null)
        {
            return null;
        }
        var range = RangeText(curRange, prevRange, date);
        var pts = (int)Math.Round((currentRate.Value - previousRate.Value) * 100, MidpointRounding.AwayFromZero);
        if (pts == 0)
        {
            return new TrendResult("flat", $"No change vs previous {Kpi.WinRateDays} days", range, "0 pts");
        }
        return new TrendResult(pts > 0 ? "up" : "down", $"{(pts > 0 ? "Up" : "Down")} {Math.Abs(pts).ToString(CultureInfo.InvariantCulture)} pts vs previous {Kpi.WinRateDays} days", range, $"{Math.Abs(pts).ToString(CultureInfo.InvariantCulture)} pts");
    }

    /// <summary>"June 2026" from yyyy-MM (English names, like the rest of the UI copy).</summary>
    private static string MonthName(string yearMonth) =>
        new DateOnly(int.Parse(yearMonth[..4], CultureInfo.InvariantCulture), int.Parse(yearMonth[5..7], CultureInfo.InvariantCulture), 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>Won revenue per month for the months from..to in the base currency, as a polyline on a zero baseline.</summary>
    public static SparkResult Spark(IReadOnlyList<AggRow> wonRows, string from, string to, Converter conv, string currentMonth, string locale)
    {
        var months = Periods.MonthsBetween(from, to, 12);
        var values = months.Select(m => conv.ConvertRows(wonRows.Where(r => (r.Month ?? currentMonth) == m), r => (r.Currency, r.Count, r.Amount), _ => m).Total).ToList();
        var max = values.Count == 0 ? 0m : values.Max();
        var step = months.Count > 1 ? 116.0 / (months.Count - 1) : 0.0;
        double X(int i) => 2 + step * i;
        double Y(decimal v) => max == 0 ? 30 : 30 - (double)(v / max) * 28;
        var points = new StringBuilder();
        for (var i = 0; i < values.Count; i++)
        {
            points.Append(CultureInfo.InvariantCulture, $"{X(i):0.##},{Y(values[i]):0.##} ");
        }
        var last = values.Count - 1;
        var text = max == 0
            ? "Last 12 months: nothing won."
            : $"Last 12 months: highest in {MonthName(months[values.IndexOf(max)])}, {NumberFormats.CompactMoney(max, conv.Base, locale)}.";
        return new SparkResult(points.ToString().TrimEnd(), last < 0 ? "2" : X(last).ToString("0.##", CultureInfo.InvariantCulture), last < 0 ? "30" : Y(values[last]).ToString("0.##", CultureInfo.InvariantCulture), text);
    }
}
