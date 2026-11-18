using System.Globalization;

namespace Crm.Web.Services;

/// <summary>Compact money and percent text for tiles and chart takeaways, pinned per locale profile (no host ICU).</summary>
public static class NumberFormats
{
    /// <summary>"DKK 120k", "DKK 1,2m": values below 1,000 keep up to one decimal; the decimal separator follows the locale.</summary>
    public static string CompactMoney(decimal value, string currency, string locale)
    {
        var p = LocaleProfile.For(locale);
        var abs = Math.Abs(value);
        var (div, suffix) = abs >= 1_000_000_000m ? (1_000_000_000m, "bn") : abs >= 1_000_000m ? (1_000_000m, "m") : abs >= 1_000m ? (1_000m, "k") : (1m, "");
        var scaled = Math.Round(value / div, 1, MidpointRounding.AwayFromZero);
        return $"{currency} {scaled.ToString("0.#", p.Number)}{suffix}";
    }

    public static string Percent(decimal fraction, string locale, int digits = 0)
    {
        var p = LocaleProfile.For(locale);
        var text = Math.Round(fraction * 100, digits, MidpointRounding.AwayFromZero).ToString(digits == 0 ? "0" : "0." + new string('0', digits), p.Number);
        return p.Code == "da-DK" ? text + " %" : text + "%";
    }

    public static string Count(int n, string locale) => n.ToString("N0", LocaleProfile.For(locale).Number);

    private static readonly string[] EnMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] DaMonths = ["jan.", "feb.", "mar.", "apr.", "maj", "jun.", "jul.", "aug.", "sep.", "okt.", "nov.", "dec."];

    /// <summary>"Nov 2026" / "nov. 2026" from yyyy-MM.</summary>
    public static string MonthLabel(string yearMonth, string locale)
    {
        var m = int.Parse(yearMonth[5..7], CultureInfo.InvariantCulture);
        return $"{(LocaleProfile.For(locale).Code == "da-DK" ? DaMonths : EnMonths)[m - 1]} {yearMonth[..4]}";
    }
}
