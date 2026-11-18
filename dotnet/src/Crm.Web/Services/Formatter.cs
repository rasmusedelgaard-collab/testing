using System.Globalization;

namespace Crm.Web.Services;

/// <summary>Port of makeFormatter. All output goes through the pinned locale profile, never CurrentCulture.</summary>
public sealed class Formatter(string localeCode, string timezone)
{
    private readonly LocaleProfile _p = LocaleProfile.For(localeCode);
    private readonly TimeZoneInfo _tz = TimeZones.Find(timezone);

    public string Money(decimal amount, string currency) => $"{currency} {amount.ToString("N2", _p.Number)}";

    /// <summary>Date-only values are calendar dates and never go through a time zone.</summary>
    public string Date(string? iso)
    {
        if (iso is { Length: >= 10 } && DateOnly.TryParseExact(iso[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d.ToString(_p.DatePattern, CultureInfo.InvariantCulture);
        }
        return "";
    }

    /// <summary>An ISO UTC timestamp (as the SQL emits it) in the Settings zone: "date time".</summary>
    public string DateTime(string? iso)
    {
        if (string.IsNullOrEmpty(iso) || !System.DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
        {
            return "";
        }
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, _tz);
        return local.ToString(_p.DatePattern, CultureInfo.InvariantCulture) + " " + local.ToString(_p.TimePattern, CultureInfo.InvariantCulture);
    }

    /// <summary>Plain amount for form inputs: locale decimal separator, no grouping, 0-2 decimals.</summary>
    public string AmountInput(decimal amount) => amount.ToString("0.##", _p.Number);

    /// <summary>Rate for form inputs: 0-6 decimals.</summary>
    public string RateInput(decimal rate) => rate.ToString("0.######", _p.Number);
}
