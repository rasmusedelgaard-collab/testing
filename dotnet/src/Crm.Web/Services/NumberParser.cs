using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Crm.Web.Services;

public enum ParseFailure { None, Format, Negative }

public readonly record struct ParseResult(bool Ok, decimal Value, ParseFailure Reason)
{
    public static ParseResult Success(decimal v) => new(true, v, ParseFailure.None);

    public static ParseResult Fail(ParseFailure r) => new(false, 0m, r);
}

/// <summary>
/// Strict decimal parsing in the Settings locale (port of parseDecimal). Financial data: never guesses.
/// Uses [0-9] (in .NET \d matches any Unicode digit) and \z (in .NET $ matches before a final newline).
/// </summary>
public static partial class NumberParser
{
    private const decimal Max = 99_999_999_999_999m;
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public static ParseResult ParseDecimal(string input, string locale, int maxDecimals, bool allowGrouping)
    {
        var p = LocaleProfile.For(locale);
        var s = input.Trim();
        if (s.StartsWith('-'))
        {
            return ParseResult.Fail(ParseFailure.Negative);
        }
        var key = $"{p.Code}|{maxDecimals}|{allowGrouping}";
        var re = Cache.GetOrAdd(key, _ =>
        {
            var g = Regex.Escape(p.GroupSep);
            var integer = allowGrouping ? $"(?:[0-9]+|[0-9]{{1,3}}(?:{g}[0-9]{{3}})+)" : "[0-9]+";
            return new Regex($"^({integer})(?:{Regex.Escape(p.DecimalSep)}([0-9]{{1,{maxDecimals}}}))?\\z", RegexOptions.CultureInvariant);
        });
        var m = re.Match(s);
        if (!m.Success)
        {
            return ParseResult.Fail(ParseFailure.Format);
        }
        var text = m.Groups[1].Value.Replace(p.GroupSep, "", StringComparison.Ordinal) + (m.Groups[2].Success ? "." + m.Groups[2].Value : "");
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) || v > Max)
        {
            return ParseResult.Fail(ParseFailure.Format);
        }
        return ParseResult.Success(v);
    }

    public static ParseResult ParseAmount(string input, string locale) => ParseDecimal(input, locale, 2, true);

    /// <summary>FX rates: up to 6 decimals (ADR-0007 P5) and no group separators at all.</summary>
    public static ParseResult ParseRate(string input, string locale) => ParseDecimal(input, locale, 6, false);

    public static string AmountExample(string locale) => 12500m.ToString("N2", LocaleProfile.For(locale).Number);

    public static string RateExample(string locale) => 7.459m.ToString("0.0000", LocaleProfile.For(locale).Number);

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    /// <summary>yyyy-MM-dd, a real calendar date, year >= 1900.</summary>
    public static bool IsRealDate(string s) =>
        IsoDate().IsMatch(s) && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year >= 1900;
}
