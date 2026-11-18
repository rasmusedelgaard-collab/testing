using System.Text.RegularExpressions;

namespace Crm.Web.Config;

public sealed record ParsedConnectionString(string Server, int? Port, string Database, string User, string Password, bool TrustServerCertificate);

public sealed class ConnectionStringException(string message) : Exception(message);

/// <summary>
/// Strict parser for ADO.NET style SQL login connection strings (port of src/db/connectionString.ts).
/// Anything unknown or weakening is refused. Messages name keys only and never contain a value (values include the password).
/// </summary>
public static partial class ConnectionStringParser
{
    private static readonly string[] ServerKeys = ["server", "datasource", "addr", "address", "networkaddress"];
    private static readonly string[] DatabaseKeys = ["database", "initialcatalog"];
    private static readonly string[] UserKeys = ["userid", "uid", "user"];
    private static readonly string[] PasswordKeys = ["password", "pwd"];
    private static readonly string[] Ignored = ["persistsecurityinfo", "multipleactiveresultsets", "connecttimeout", "connectiontimeout", "timeout", "applicationname", "app", "pooling", "workstationid", "wsid"];
    private static readonly string[] Refused = ["integratedsecurity", "trustedconnection", "authentication", "attachdbfilename", "failoverpartner", "applicationintent", "multisubnetfailover"];

    [GeneratedRegex(@"^(?:tcp:)?([A-Za-z0-9._-]+)(?:,([0-9]{1,5}))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ServerPattern();

    private static List<(string Key, string Value)> ReadPairs(string input)
    {
        var pairs = new List<(string, string)>();
        var i = 0;
        var n = input.Length;
        while (i < n)
        {
            while (i < n && (input[i] == ';' || char.IsWhiteSpace(input[i])))
            {
                i++;
            }
            if (i >= n)
            {
                break;
            }
            var eq = input.IndexOf('=', i);
            if (eq < 0)
            {
                throw new ConnectionStringException("malformed: a segment has no \"=\"");
            }
            var key = input[i..eq].Trim();
            if (key.Length == 0 || key.Contains(';', StringComparison.Ordinal))
            {
                throw new ConnectionStringException("malformed: empty or invalid key");
            }
            i = eq + 1;
            while (i < n && char.IsWhiteSpace(input[i]))
            {
                i++;
            }
            string value;
            var quote = i < n ? input[i] : '\0';
            if (quote is '"' or '\'')
            {
                i++;
                var sb = new System.Text.StringBuilder();
                for (; ; )
                {
                    if (i >= n)
                    {
                        throw new ConnectionStringException($"malformed: unterminated quoted value for \"{key}\"");
                    }
                    if (input[i] == quote)
                    {
                        if (i + 1 < n && input[i + 1] == quote)
                        {
                            sb.Append(quote);
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    sb.Append(input[i]);
                    i++;
                }
                value = sb.ToString();
                while (i < n && char.IsWhiteSpace(input[i]))
                {
                    i++;
                }
                if (i < n && input[i] != ';')
                {
                    throw new ConnectionStringException($"malformed: text after a quoted value for \"{key}\"");
                }
            }
            else
            {
                var end = input.IndexOf(';', i);
                value = input[i..(end < 0 ? n : end)].Trim();
                i = end < 0 ? n : end;
            }
            pairs.Add((key, value));
        }
        return pairs;
    }

    private static bool Truthy(string v) => v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v == "1";
    private static bool Falsy(string v) => v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase) || v == "0";

    public static ParsedConnectionString Parse(string input)
    {
        if (input.Length > 2000)
        {
            throw new ConnectionStringException("too long");
        }
        string? server = null, database = null, user = null, password = null;
        int? port = null;
        var trust = false;
        var seen = new HashSet<string>();
        foreach (var (rawKey, value) in ReadPairs(input))
        {
            var key = WhitespacePattern().Replace(rawKey.ToLowerInvariant(), "");
            if (!seen.Add(key))
            {
                throw new ConnectionStringException($"duplicate key \"{rawKey}\"");
            }
            if (ServerKeys.Contains(key))
            {
                var m = ServerPattern().Match(value);
                if (!m.Success)
                {
                    throw new ConnectionStringException($"unsupported \"{rawKey}\" value (expected host or host,port)");
                }
                server = m.Groups[1].Value;
                if (m.Groups[2].Success)
                {
                    var p = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (p is < 1 or > 65535)
                    {
                        throw new ConnectionStringException("port must be between 1 and 65535");
                    }
                    port = p;
                }
            }
            else if (DatabaseKeys.Contains(key))
            {
                database = Bounded(rawKey, value, 128);
            }
            else if (UserKeys.Contains(key))
            {
                user = Bounded(rawKey, value, 128);
            }
            else if (PasswordKeys.Contains(key))
            {
                password = Bounded(rawKey, value, 256);
            }
            else if (key == "trustservercertificate")
            {
                if (!Truthy(value) && !Falsy(value))
                {
                    throw new ConnectionStringException("invalid \"TrustServerCertificate\" (use True or False)");
                }
                trust = Truthy(value);
            }
            else if (key == "encrypt")
            {
                if (Falsy(value))
                {
                    throw new ConnectionStringException("\"Encrypt=False\" is not supported: connections are always encrypted");
                }
                if (!Truthy(value) && !EncryptPattern().IsMatch(value))
                {
                    throw new ConnectionStringException("invalid \"Encrypt\"");
                }
            }
            else if (Ignored.Contains(key))
            {
                continue;
            }
            else if (Refused.Contains(key))
            {
                throw new ConnectionStringException($"\"{rawKey}\" is not supported (SQL login strings only)");
            }
            else
            {
                throw new ConnectionStringException($"unknown key \"{rawKey}\"");
            }
        }
        var missing = new List<string>();
        if (string.IsNullOrEmpty(server)) { missing.Add("Server"); }
        if (string.IsNullOrEmpty(database)) { missing.Add("Database"); }
        if (string.IsNullOrEmpty(user)) { missing.Add("User Id"); }
        if (string.IsNullOrEmpty(password)) { missing.Add("Password"); }
        if (missing.Count > 0)
        {
            throw new ConnectionStringException($"missing {string.Join(", ", missing)}");
        }
        return new ParsedConnectionString(server!, port, database!, user!, password!, trust);
    }

    private static string Bounded(string rawKey, string value, int max)
    {
        if (value.Length == 0 || value.Length > max)
        {
            throw new ConnectionStringException($"invalid \"{rawKey}\"");
        }
        return value;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"^(strict|mandatory|optional)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EncryptPattern();
}
