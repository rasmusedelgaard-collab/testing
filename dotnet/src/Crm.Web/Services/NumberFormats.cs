using System.Globalization;

namespace Crm.Web.Services;

/// <summary>Compact money and percent text for tiles and chart takeaways, pinned per locale profile (no host ICU).</summary>
public static class NumberFormats
{
    private static readonly string[] EnSuffix = ["", "K", "M", "B"];
    private static readonly string[] DaSuffix = ["", " t", " mio.", " mia."];

    /// <summary>
    /// UX spec 3.x: en-GB/en-US "DKK 2.4M" (K, M, B); da-DK "2,4 mio. kr." ("t", "mio.", "mia."), like ICU. 0-1 decimals.
    /// The value is rounded before the scale is chosen, so 999,960 shows "DKK 1M" and never "1000K".
    /// </summary>
    public static string CompactMoney(decimal value, string currency, string locale)
    {
        var p = LocaleProfile.For(locale);
        var abs = Math.Abs(value);
        var scale = abs >= 1_000_000_000m ? 3 : abs >= 1_000_000m ? 2 : abs >= 1_000m ? 1 : 0;
        var rounded = Math.Round(abs / Pow1000(scale), 1, MidpointRounding.AwayFromZero);
        if (rounded >= 1000m && scale < 3)
        {
            scale++;
            rounded = Math.Round(abs / Pow1000(scale), 1, MidpointRounding.AwayFromZero);
        }
        var number = (value < 0 ? -rounded : rounded).ToString("0.#", p.Number);
        if (p.Code == "da-DK")
        {
            return $"{number}{DaSuffix[scale]} {(currency == "DKK" ? "kr." : currency)}";
        }
        return $"{currency} {number}{EnSuffix[scale]}";
    }

    private static decimal Pow1000(int n) => n switch { 0 => 1m, 1 => 1_000m, 2 => 1_000_000m, _ => 1_000_000_000m };

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
