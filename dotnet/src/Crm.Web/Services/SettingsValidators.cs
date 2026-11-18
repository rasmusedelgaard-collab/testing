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
        if (!Accent().IsMatch(accent)) { errors["accentColor"] = "Enter a colour like #1D4ED8."; }
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

    public const int MaxLogoBytes = 512_000;
    private const string BadType = "Logo must be a PNG, 500 KB or smaller.";
    private const string SvgRefused = "SVG logos are not supported. Upload a PNG, 500 KB or smaller.";

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
            return (true, null, System.Security.Cryptography.SHA256.HashData(buf));
        }
        var head = System.Text.Encoding.UTF8.GetString(buf[..Math.Min(512, buf.Length)]).ToLowerInvariant();
        return head.Contains("<svg", StringComparison.Ordinal) || head.Contains("<?xml", StringComparison.Ordinal) ? (false, SvgRefused, null) : (false, BadType, null);
    }
}
