using System.Globalization;

namespace Crm.Web.Services;

public readonly record struct DateRange(string From, string To);

public sealed record PeriodResult(bool Ok, string Key, DateRange Range, string? Error = null);

/// <summary>Calendar arithmetic on DateOnly (no time zones: callers pass "today" in the Settings time zone). Port of periods.ts.</summary>
public static class Periods
{
    public static readonly string[] Keys = ["this-month", "last-month", "this-quarter", "last-quarter", "this-year", "last-12-months", "custom"];

    public static readonly IReadOnlyDictionary<string, string> Label = new Dictionary<string, string>
    {
        ["this-month"] = "This month",
        ["last-month"] = "Last month",
        ["this-quarter"] = "This quarter",
        ["last-quarter"] = "Last quarter",
        ["this-year"] = "This year",
        ["last-12-months"] = "Last 12 months",
        ["custom"] = "Custom",
    };

    private static DateOnly Parse(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Ymd(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string AddDays(string date, int days) => Ymd(Parse(date).AddDays(days));

    /// <summary>First day of the month that is <paramref name="months"/> after the month of <paramref name="date"/>.</summary>
    public static string MonthStartPlus(string date, int months)
    {
        var d = Parse(date);
        return Ymd(new DateOnly(d.Year, d.Month, 1).AddMonths(months));
    }

    public static string MonthOf(string date) => date[..7];

    /// <summary>Last day of the period that starts on <paramref name="start"/> and lasts <paramref name="months"/> months.</summary>
    public static string EndAfterMonths(string start, int months) => AddDays(MonthStartPlus(start, months), -1);

    public static DateRange MonthRange(string today)
    {
        var from = MonthStartPlus(today, 0);
        return new DateRange(from, EndAfterMonths(from, 1));
    }

    /// <summary>Fiscal quarter containing today: quarters start in the fiscal start month and every 3 months after it.</summary>
    public static DateRange QuarterRange(string today, int fiscalStartMonth)
    {
        var m = Parse(today).Month;
        var monthsBack = (m - fiscalStartMonth + 12) % 12 % 3;
        var from = MonthStartPlus(today, -monthsBack);
        return new DateRange(from, EndAfterMonths(from, 3));
    }

    public static DateRange FiscalYearRange(string today, int fiscalStartMonth)
    {
        var monthsBack = (Parse(today).Month - fiscalStartMonth + 12) % 12;
        var from = MonthStartPlus(today, -monthsBack);
        return new DateRange(from, EndAfterMonths(from, 12));
    }

    /// <summary>Resolves the Analytics period. Invalid custom ranges fall back to this quarter and report the exact UI copy.</summary>
    public static PeriodResult Resolve(string period, string from, string to, string shown, string today, int fiscalStartMonth)
    {
        var key = Keys.Contains(period) ? period : "this-quarter";
        // The page pre-fills From/To with the preset's range and remembers the preset (`shown`). Unchanged preset but edited dates = custom;
        // a changed preset means the old pre-filled dates are stale and ignored.
        if (key != "custom" && shown == key && (from.Length > 0 || to.Length > 0))
        {
            var preset = Resolve(key, "", "", "", today, fiscalStartMonth).Range;
            if (from != preset.From || to != preset.To)
            {
                key = "custom";
            }
        }
        switch (key)
        {
            case "this-month": return new PeriodResult(true, key, MonthRange(today));
            case "last-month": return new PeriodResult(true, key, MonthRange(MonthStartPlus(today, -1)));
            case "this-quarter": return new PeriodResult(true, key, QuarterRange(today, fiscalStartMonth));
            case "last-quarter": return new PeriodResult(true, key, QuarterRange(MonthStartPlus(today, -3), fiscalStartMonth));
            case "this-year": return new PeriodResult(true, key, FiscalYearRange(today, fiscalStartMonth));
            case "last-12-months": return new PeriodResult(true, key, new DateRange(MonthStartPlus(today, -11), EndAfterMonths(MonthStartPlus(today, -11), 12)));
            default:
                var fallback = QuarterRange(today, fiscalStartMonth);
                if (!NumberParser.IsRealDate(from) || !NumberParser.IsRealDate(to))
                {
                    return new PeriodResult(false, key, fallback, "Enter a real date, for example 31.12.2026.");
                }
                if (string.CompareOrdinal(to, from) < 0)
                {
                    return new PeriodResult(false, key, fallback, "End date must be on or after start date.");
                }
                if (string.CompareOrdinal(to, AddDays(MonthStartPlus(from, 0), 366 * 5 + 2)) > 0)
                {
                    return new PeriodResult(false, key, fallback, "Choose a range of 5 years or less.");
                }
                return new PeriodResult(true, key, new DateRange(from, to));
        }
    }

    /// <summary>Every yyyy-MM from the month of from to the month of to (at most max).</summary>
    public static IReadOnlyList<string> MonthsBetween(string from, string to, int max = 60)
    {
        var list = new List<string>();
        for (var m = MonthStartPlus(from, 0); string.CompareOrdinal(m, to) <= 0 && list.Count < max; m = MonthStartPlus(m, 1))
        {
            list.Add(MonthOf(m));
        }
        return list;
    }
}
