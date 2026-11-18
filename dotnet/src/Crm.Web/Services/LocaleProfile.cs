using System.Globalization;

namespace Crm.Web.Services;

/// <summary>
/// Separators and patterns for the three allowed locales are pinned in code: Windows NLS and different ICU versions
/// format differently (da-DK dates as 18-11-2026), and the Node app gave 18.11.2026 (dotnet.md 11.1).
/// </summary>
public sealed class LocaleProfile
{
    public static readonly IReadOnlyList<string> Allowed = ["da-DK", "en-GB", "en-US"];

    public string Code { get; }
    public string GroupSep { get; }
    public string DecimalSep { get; }
    public string DatePattern { get; }
    public string TimePattern { get; }
    public NumberFormatInfo Number { get; }

    private LocaleProfile(string code, string group, string dec, string datePattern, string timePattern)
    {
        Code = code;
        GroupSep = group;
        DecimalSep = dec;
        DatePattern = datePattern;
        TimePattern = timePattern;
        Number = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        Number.NumberGroupSeparator = group;
        Number.NumberDecimalSeparator = dec;
        Number.NumberGroupSizes = [3];
        Number.NegativeSign = "-";
    }

    private static readonly LocaleProfile Da = new("da-DK", ".", ",", "dd'.'MM'.'yyyy", "HH':'mm");
    private static readonly LocaleProfile Gb = new("en-GB", ",", ".", "dd'/'MM'/'yyyy", "HH':'mm");
    private static readonly LocaleProfile Us = new("en-US", ",", ".", "MM'/'dd'/'yyyy", "h':'mm tt");

    /// <summary>Unknown locale codes fall back to en-GB (as the Node app does).</summary>
    public static LocaleProfile For(string? code) => code switch { "da-DK" => Da, "en-US" => Us, _ => Gb };
}
