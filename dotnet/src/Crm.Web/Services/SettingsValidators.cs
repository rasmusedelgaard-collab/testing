using System.Text.RegularExpressions;

namespace Crm.Web.Services;

/// <summary>Validators for the settings pages (port of settingsValidators.ts, increment 4 subset). Identical messages.</summary>
public static partial class SettingsValidators
{
    [GeneratedRegex(@"^#[0-9a-fA-F]{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Accent();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Guid();

    [GeneratedRegex(@"[{}\s]", RegexOptions.CultureInvariant)]
    private static partial Regex Junk();

    public static (string AppName, string AccentColor, Dictionary<string, string> Errors) ValidateGeneral(string? appNameRaw, string? accentRaw)
    {
        var errors = new Dictionary<string, string>();
        var appName = (appNameRaw ?? "").Trim();
        if (appName.Length == 0) { errors["appName"] = "Enter a CRM name."; }
        else if (appName.Length > 40) { errors["appName"] = "CRM name must be 40 characters or fewer."; }
        var accent = (accentRaw ?? "").Trim();
        if (!Accent().IsMatch(accent)) { errors["accentColor"] = "Enter a colour like #1A66CC."; }
        else if (Color.AccentContrast(accent) < 4.5) { errors["accentColor"] = "This colour is too light for text and buttons. Choose a darker shade."; }
        return (appName, accent.ToUpperInvariant(), errors);
    }

    public static (string Name, string GroupId, Dictionary<string, string> Errors) ValidateTeam(string? nameRaw, string? groupRaw)
    {
        var errors = new Dictionary<string, string>();
        var name = (nameRaw ?? "").Trim();
        if (name.Length == 0) { errors["name"] = "Enter a team name."; }
        else if (name.Length > 80) { errors["name"] = "Team name must be 80 characters or fewer."; }
        var groupId = Junk().Replace(groupRaw ?? "", "").ToLowerInvariant();
        if (groupId.Length == 0) { errors["groupId"] = "Enter the Entra group object ID."; }
        else if (!Guid().IsMatch(groupId)) { errors["groupId"] = "Enter the group object ID in this format: 00000000-0000-0000-0000-000000000000."; }
        return (name, groupId, errors);
    }

    public static readonly IReadOnlyList<string> Currencies = ["DKK", "EUR", "USD", "GBP", "SEK", "NOK", "CHF", "PLN", "CZK", "HUF", "ISK", "CAD", "AUD", "JPY", "CNY", "INR", "BRL", "ZAR", "SGD", "AED"];

    public sealed record LocaleValues(string Base, IReadOnlyList<string> Enabled, string Locale, string Timezone, string FirstDay, string FiscalStart);

    private static string T(string? s) => (s ?? "").Trim();

    private static bool InRange(string s, int min, int max) =>
        int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= min && n <= max;

    public static (LocaleValues Values, Dictionary<string, string> Errors) ValidateLocale(string? baseRaw, IEnumerable<string>? enabledRaw, string? localeRaw, string? timezoneRaw, string? firstDayRaw, string? fiscalRaw, IReadOnlyCollection<string> zones)
    {
        var errors = new Dictionary<string, string>();
        var baseCurrency = T(baseRaw);
        if (!Currencies.Contains(baseCurrency)) { errors["base"] = "Choose a base currency."; }
        var enabled = (enabledRaw ?? []).Select(T).Where(Currencies.Contains).Distinct().ToList();
        if (!errors.ContainsKey("base") && !enabled.Contains(baseCurrency)) { enabled.Insert(0, baseCurrency); } // the base currency is always enabled
        var locale = T(localeRaw);
        if (!LocaleProfile.Allowed.Contains(locale)) { errors["locale"] = "Choose a locale."; }
        var timezone = T(timezoneRaw);
        if (!zones.Contains(timezone)) { errors["timezone"] = "Choose a timezone."; }
        var firstDay = T(firstDayRaw);
        if (!InRange(firstDay, 1, 7)) { errors["firstDay"] = "Choose the first day of the week."; }
        var fiscal = T(fiscalRaw);
        if (!InRange(fiscal, 1, 12)) { errors["fiscalStart"] = "Choose a month."; }
        return (new LocaleValues(baseCurrency, enabled, locale, timezone, firstDay, fiscal), errors);
    }

    [GeneratedRegex(@"^[0-9]{4}-(0[1-9]|1[0-2])\z", RegexOptions.CultureInvariant)]
    private static partial Regex MonthPattern();

    public static (string Currency, string Month, decimal Rate, Dictionary<string, string> Errors) ValidateFx(string? currencyRaw, string? monthRaw, string? rateRaw, string baseCurrency, IReadOnlyList<string> enabled, string locale)
    {
        var errors = new Dictionary<string, string>();
        var currency = T(currencyRaw).ToUpperInvariant();
        if (currency.Length == 0 || currency == baseCurrency || !enabled.Contains(currency)) { errors["currency"] = "Choose a currency."; }
        var month = T(monthRaw);
        if (!MonthPattern().IsMatch(month) || int.Parse(month[..4], System.Globalization.CultureInfo.InvariantCulture) < 2000) { errors["month"] = "Choose a month."; }
        var rate = 0m;
        var raw = T(rateRaw);
        var example = $"Enter a rate, for example {NumberParser.RateExample(locale)}.";
        var r = raw.Length > 0 ? NumberParser.ParseRate(raw, locale) : default;
        if (raw.Length == 0 || (!r.Ok && r.Reason == ParseFailure.Format)) { errors["rate"] = example; }
        else if (r.Ok)
        {
            rate = r.Value;
            if (rate <= 0) { errors["rate"] = "Rate must be more than 0."; }
            else if (rate >= 1e10m) { errors["rate"] = example; }
        }
        else { errors["rate"] = "Rate must be more than 0."; }
        return (currency, month, rate, errors);
    }

    public static string? ValidateLabel(string? raw)
    {
        var t = T(raw);
        return t.Length == 0 ? "Enter a name." : t.Length > 80 ? "Name must be 80 characters or fewer." : null;
    }

    public sealed record StageForm(string Id, string Name, string Prob, string Type);

    public sealed record StageInput(int? Id, string Name, int Probability, string Type);

    /// <summary>Rows come from the editor form. Won is forced to 100 and Lost to 0 (the database CHECKs require it).</summary>
    public static (IReadOnlyList<StageInput> Rows, Dictionary<string, string> Errors, List<string> Summary) ValidateStages(IReadOnlyList<StageForm> rows)
    {
        var errors = new Dictionary<string, string>();
        var summary = new List<string>();
        var rowsOut = new List<StageInput>();
        var seen = new HashSet<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int? id = StageId().IsMatch(r.Id) ? (long.Parse(r.Id, System.Globalization.CultureInfo.InvariantCulture) > int.MaxValue ? int.MaxValue : int.Parse(r.Id, System.Globalization.CultureInfo.InvariantCulture)) : null;
            var name = T(r.Name);
            var type = r.Type is "won" or "lost" ? r.Type : "open";
            var prob = StageProb().IsMatch(T(r.Prob)) ? int.Parse(T(r.Prob), System.Globalization.CultureInfo.InvariantCulture) : -1;
            if (name.Length == 0) { errors[$"name{i}"] = "Enter a stage name."; }
            else if (name.Length > 60) { errors[$"name{i}"] = "Stage name must be 60 characters or fewer."; }
            else if (name.StartsWith('~')) { errors[$"name{i}"] = "Stage name can't start with ~."; } // "~id" is the placeholder used while saving
            else if (seen.Contains(name.ToLowerInvariant())) { errors[$"name{i}"] = "Each stage needs a different name."; }
            seen.Add(name.ToLowerInvariant());
            if (type == "won") { prob = 100; }
            else if (type == "lost") { prob = 0; }
            else if (prob is < 0 or > 100) { errors[$"prob{i}"] = "Probability must be between 0 and 100."; }
            rowsOut.Add(new StageInput(id, name, prob, type));
        }
        if (rowsOut.Count(r => r.Type == "won") != 1 || rowsOut.Count(r => r.Type == "lost") != 1) { summary.Add("You need exactly one Won stage and one Lost stage."); }
        if (!rowsOut.Any(r => r.Type == "open")) { summary.Add("You need at least one open stage."); }
        return (rowsOut, errors, summary);
    }

    [GeneratedRegex(@"^[1-9][0-9]{0,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex StageId();

    [GeneratedRegex(@"^[0-9]{1,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex StageProb();

    public const int MaxLogoBytes = 512_000;
    private const string BadType = "Logo must be a PNG, 500 KB or smaller.";
    private const string SvgRefused = "SVG logos are not supported. Upload a PNG, 500 KB or smaller.";

    public const int MaxLogoPixels = 1024;

    /// <summary>
    /// Structure check after the signature: IHDR first (13 bytes, width and height 1..1024), then chunks that stay inside the buffer,
    /// ending exactly with an IEND chunk. A truncated or padded file, or an image decompressing to a huge bitmap, is refused.
    /// </summary>
    private static bool IsWellFormedPng(ReadOnlySpan<byte> buf)
    {
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buf[8..12]) != 13)
        {
            return false;
        }
        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buf[16..20]);
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buf[20..24]);
        if (width is 0 or > MaxLogoPixels || height is 0 or > MaxLogoPixels)
        {
            return false;
        }
        var pos = 8L;
        while (pos + 12 <= buf.Length)
        {
            var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buf.Slice((int)pos, 4));
            var next = pos + 12 + len;
            if (next > buf.Length)
            {
                return false;
            }
            if (buf.Slice((int)pos + 4, 4).SequenceEqual("IEND"u8))
            {
                return len == 0 && next == buf.Length;
            }
            pos = next;
        }
        return false;
    }

    /// <summary>
    /// PNG only. SVG can carry script and external references a text filter cannot reliably remove, so it is refused outright.
    /// The type is decided by content (magic bytes plus a first IHDR chunk), never by filename or declared MIME type.
    /// </summary>
    public static (bool Ok, string? Message, byte[]? Sha256) CheckLogo(ReadOnlySpan<byte> buf)
    {
        if (buf.Length is 0 or > MaxLogoBytes)
        {
            return (false, BadType, null);
        }
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (buf.Length >= 33 && buf[..8].SequenceEqual(png) && buf[12..16].SequenceEqual("IHDR"u8))
        {
            return IsWellFormedPng(buf) ? (true, null, System.Security.Cryptography.SHA256.HashData(buf)) : (false, BadType, null);
        }
        var head = System.Text.Encoding.UTF8.GetString(buf[..Math.Min(512, buf.Length)]).ToLowerInvariant();
        return head.Contains("<svg", StringComparison.Ordinal) || head.Contains("<?xml", StringComparison.Ordinal) ? (false, SvgRefused, null) : (false, BadType, null);
    }
}
