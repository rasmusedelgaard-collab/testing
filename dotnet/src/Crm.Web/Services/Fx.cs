using System.Globalization;

namespace Crm.Web.Services;

public sealed record FxRateRow(string Currency, string Month, decimal Rate);

public readonly record struct Conversion(bool Ok, decimal Value, bool Fallback);

public sealed record MoneyRow(string Currency, int Count, decimal Amount);

public sealed record Converted(decimal Total, IReadOnlyList<(string Currency, int Count)> Excluded, IReadOnlyList<string> FallbackCurrencies);

/// <summary>
/// ADR-0005: 1 CURRENCY = rate BASE. The base currency is always exactly 1 and never looked up. No rate at or before the
/// month means "not convertible" (the caller must exclude and say so, never use 1:1). Money is decimal end to end.
/// </summary>
public sealed class Converter
{
    private readonly Dictionary<string, List<FxRateRow>> _byCurrency = [];

    public Converter(string baseCurrency, IEnumerable<FxRateRow> rates)
    {
        Base = baseCurrency;
        foreach (var r in rates)
        {
            if (r.Currency == baseCurrency || r.Rate <= 0)
            {
                continue;
            }
            if (!_byCurrency.TryGetValue(r.Currency, out var list)) { _byCurrency[r.Currency] = list = []; }
            list.Add(r);
        }
        foreach (var l in _byCurrency.Values)
        {
            l.Sort((a, b) => string.CompareOrdinal(b.Month, a.Month)); // newest first
        }
    }

    public string Base { get; }

    /// <summary>Converts at the rate of <paramref name="month"/> (yyyy-MM); falls back to the latest EARLIER month; never guesses.</summary>
    public Conversion Convert(decimal amount, string currency, string month)
    {
        if (currency == Base)
        {
            return new Conversion(true, amount, false);
        }
        var hit = _byCurrency.TryGetValue(currency, out var list) ? list.Find(r => string.CompareOrdinal(r.Month, month) <= 0) : null;
        return hit is null ? new Conversion(false, 0, false) : new Conversion(true, amount * hit.Rate, hit.Month != month);
    }

    public static decimal Round2(decimal n) => Math.Round(n, 2, MidpointRounding.AwayFromZero);

    /// <summary>Sums rows in the base currency. monthOf picks the rate month per row; weight scales a row (e.g. stage probability).</summary>
    public Converted ConvertRows<TRow>(IEnumerable<TRow> rows, Func<TRow, (string Currency, int Count, decimal Amount)> select, Func<TRow, string> monthOf, Func<TRow, decimal>? weight = null)
    {
        decimal total = 0;
        var excluded = new Dictionary<string, int>(StringComparer.Ordinal);
        var fallback = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var (currency, count, amount) = select(r);
            var c = Convert(amount * (weight?.Invoke(r) ?? 1m), currency, monthOf(r));
            if (!c.Ok)
            {
                excluded[currency] = excluded.GetValueOrDefault(currency) + count;
                continue;
            }
            total += c.Value;
            if (c.Fallback)
            {
                fallback.Add(currency);
            }
        }
        return new Converted(
            Round2(total),
            excluded.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value)).ToList(),
            fallback.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>"3 opportunities in SEK not included: no FX rate" (UI spec 6), or "" when nothing was excluded.</summary>
    public static string ExclusionNote(Converted c)
    {
        if (c.Excluded.Count == 0)
        {
            return "";
        }
        var parts = c.Excluded.Select(e => $"{e.Count.ToString(CultureInfo.InvariantCulture)} {(e.Count == 1 ? "opportunity" : "opportunities")} in {e.Currency}");
        return $"{string.Join(", ", parts)} not included: no FX rate";
    }

    public static string FallbackNote(Converted c)
    {
        var n = c.FallbackCurrencies.Count;
        return n == 0 ? "" : $"Uses latest rate for {n.ToString(CultureInfo.InvariantCulture)} {(n == 1 ? "currency" : "currencies")}";
    }
}
