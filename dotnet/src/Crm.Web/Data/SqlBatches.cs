using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Crm.Web.Data;

/// <summary>Port of scripts/sqlBatches.ts.</summary>
public static partial class SqlBatches
{
    [GeneratedRegex(@"^\s*GO\s*(--.*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoLine();

    [GeneratedRegex(@"^([0-9]{3})_(.+)\.sql\z", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationName();

    /// <summary>Splits a T-SQL script on lines containing only GO (case-insensitive), dropping empty batches.</summary>
    public static IReadOnlyList<string> Split(string script)
    {
        var batches = new List<string>();
        var current = new List<string>();
        foreach (var line in script.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (GoLine().IsMatch(line))
            {
                batches.Add(string.Join('\n', current));
                current.Clear();
            }
            else
            {
                current.Add(line);
            }
        }
        batches.Add(string.Join('\n', current));
        return batches.Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
    }

    /// <summary>SHA-256 over the raw bytes after replacing every 0D 0A with 0A (a BOM, if any, is kept), lower-case hex.</summary>
    public static string Checksum(ReadOnlySpan<byte> bytes)
    {
        var normalised = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0D && i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
            {
                continue;
            }
            normalised.Add(bytes[i]);
        }
        return Convert.ToHexStringLower(SHA256.HashData(normalised.ToArray()));
    }

    public static (int Version, string Name)? ParseMigrationName(string file)
    {
        var m = MigrationName().Match(file);
        return m.Success ? (int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value) : null;
    }
}
