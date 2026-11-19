using System.Globalization;
using System.Text.RegularExpressions;

namespace Crm.Web.Services;

/// <summary>Port of color.ts: WCAG contrast and the hover colour.</summary>
public static partial class Color
{
    /// <summary>Light gray panel ground (surface-panel): the darkest ground an accent is drawn on.</summary>
    public const string PanelBg = "#D6D7D7";

    /// <summary>DAFA blue: the accent when none is set.</summary>
    public const string DefaultAccent = "#00456C";

    /// <summary>The old default; treated as "not set" so the DAFA blue shows (UI redesign B-12).</summary>
    public const string LegacyAccent = "#1D4ED8";

    [GeneratedRegex(@"^#([0-9a-fA-F]{6})\z", RegexOptions.CultureInvariant)]
    private static partial Regex HexPattern();

    public static (int R, int G, int B)? ParseHex(string hex)
    {
        var m = HexPattern().Match(hex);
        if (!m.Success)
        {
            return null;
        }
        var n = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((n >> 16) & 255, (n >> 8) & 255, n & 255);
    }

    private static double Luminance((int R, int G, int B) rgb)
    {
        static double Ch(int v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(rgb.R) + 0.7152 * Ch(rgb.G) + 0.0722 * Ch(rgb.B);
    }

    public static double ContrastAgainst(string hex, string bg)
    {
        if (ParseHex(hex) is not { } a || ParseHex(bg) is not { } b)
        {
            return 0;
        }
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static double ContrastWithWhite(string hex) => ContrastAgainst(hex, "#FFFFFF");

    /// <summary>Drawn as text/links on white, the subtle gray and the panel gray (#D6D7D7, the darkest), so it must pass on white and on the panel.</summary>
    public static double AccentContrast(string hex) => Math.Min(ContrastAgainst(hex, "#FFFFFF"), ContrastAgainst(hex, PanelBg));

    /// <summary>The accent that theme.css applies: a valid colour that passes the contrast rule and is not the legacy default; otherwise null (DAFA blue).</summary>
    public static string? EffectiveAccent(string? stored) =>
        stored is not null && IsHexColor(stored) && !stored.Equals(LegacyAccent, StringComparison.OrdinalIgnoreCase) && !stored.Equals(DefaultAccent, StringComparison.OrdinalIgnoreCase) && AccentContrast(stored) >= 4.5 ? stored.ToUpperInvariant() : null;

    public static string Darken(string hex, double amount = 0.15)
    {
        var (r, g, b) = ParseHex(hex) ?? (29, 78, 216);
        string H(int v) => ((int)Math.Round(v * (1 - amount), MidpointRounding.AwayFromZero)).ToString("X2", CultureInfo.InvariantCulture);
        return "#" + H(r) + H(g) + H(b);
    }

    public static bool IsHexColor(string value) => HexPattern().IsMatch(value);
}
