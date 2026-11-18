using System.Globalization;
using System.Text.RegularExpressions;
using Crm.Web.Services;
using Microsoft.Extensions.Configuration;

namespace Crm.Web.Config;

/// <summary>
/// The only place that reads configuration keys. A pure function over key/value pairs (port of src/config.ts).
/// Messages name keys only, never values.
/// </summary>
public static partial class CrmConfigLoader
{
    private static readonly string[] LogLevels = ["fatal", "error", "warn", "info", "debug", "trace", "silent"];

    public static CrmConfig Load(IConfiguration configuration) => Load(k => configuration[k]);

    public static CrmConfig Load(IReadOnlyDictionary<string, string?> values) => Load(k => values.TryGetValue(k, out var v) ? v : null);

    public static CrmConfig Load(Func<string, string?> get)
    {
        string? Get(string k) => string.IsNullOrWhiteSpace(get(k)) ? null : get(k)!.Trim();
        var issues = new List<string>();
        var notices = new List<string>();

        var environment = Get("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var live = ParseBool(Get("LIVE"), "LIVE", issues) || Get("WEBSITE_INSTANCE_ID") is not null;
        var onAzure = Get("WEBSITE_INSTANCE_ID") is not null || Get("WEBSITE_SITE_NAME") is not null;

        if (ParseBool(Get("DEMO_MODE"), "DEMO_MODE", issues))
        {
            throw new ConfigException(["DEMO_MODE is not supported in the .NET app (there is no demo or auth bypass); use test-access-code mode with a database"]);
        }
        if (environment == "Testing" && (live || onAzure))
        {
            throw new ConfigException(["ASPNETCORE_ENVIRONMENT=Testing is refused in a live/Azure environment"]);
        }

        var authModeText = Get("AUTH_MODE") ?? "entra";
        var authMode = AuthMode.Entra;
        switch (authModeText)
        {
            case "entra": break;
            case "test-access-code": authMode = AuthMode.TestAccessCode; break;
            default: issues.Add("AUTH_MODE: must be entra or test-access-code"); break;
        }
        var testAccess = authMode == AuthMode.TestAccessCode;
        var entraSecret = Get("ENTRA_CLIENT_SECRET");
        if (testAccess)
        {
            if (live)
            {
                throw new ConfigException(["AUTH_MODE=test-access-code is refused when LIVE=true or on Azure App Service"]);
            }
            if (entraSecret is not null)
            {
                throw new ConfigException(["AUTH_MODE=test-access-code is refused when ENTRA_CLIENT_SECRET is set (this looks like a real environment)"]);
            }
        }
        if (live && Get("DB_AUTH_MODE") == "sql-password")
        {
            throw new ConfigException(["DB_AUTH_MODE=sql-password is refused when LIVE=true or on Azure App Service: use managed-identity"]);
        }

        var logLevel = Get("LOG_LEVEL") ?? "info";
        if (!LogLevels.Contains(logLevel))
        {
            issues.Add("LOG_LEVEL: must be one of " + string.Join(", ", LogLevels));
        }

        var isProduction = environment == "Production";
        var idle = ParseInt(Get("SESSION_IDLE_MINUTES"), "SESSION_IDLE_MINUTES", 1, 1440, 60, issues);
        var absolute = ParseInt(Get("SESSION_ABSOLUTE_HOURS"), "SESSION_ABSOLUTE_HOURS", 1, 168, 10, issues);
        int? trustProxyConfigured = Get("TRUST_PROXY") is null ? null : ParseInt(Get("TRUST_PROXY"), "TRUST_PROXY", 0, 5, 0, issues);

        var redirectUri = Get("REDIRECT_URI");
        var baseUrl = "";
        var secure = false;
        if (redirectUri is null)
        {
            if (!testAccess || isProduction)
            {
                issues.Add("REDIRECT_URI: required (an https URL in production; its origin is also used for cookies and HSTS)");
            }
        }
        else if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            issues.Add("REDIRECT_URI: must be an absolute http(s) URL");
        }
        else
        {
            baseUrl = uri.GetLeftPart(UriPartial.Authority);
            secure = uri.Scheme == Uri.UriSchemeHttps;
            if (isProduction && !secure)
            {
                issues.Add("REDIRECT_URI: must use https in production");
            }
            if (uri.AbsolutePath != "/auth/redirect")
            {
                issues.Add("REDIRECT_URI: the path must be /auth/redirect");
            }
        }

        var behindIis = Get("APP_POOL_ID") is not null || Get("ASPNETCORE_IIS_PHYSICAL_PATH") is not null;
        var trustProxy = trustProxyConfigured ?? (behindIis ? 0 : (secure ? 1 : 0));

        string? testCode = null;
        if (testAccess)
        {
            var code = get("TEST_ACCESS_CODE") ?? "";
            if (code.Length < 24)
            {
                issues.Add("TEST_ACCESS_CODE: required (at least 24 characters) when AUTH_MODE=test-access-code");
            }
            else if (code.EnumerateRunes().Distinct().Count() < 10)
            {
                issues.Add("TEST_ACCESS_CODE: too repetitive (use at least 10 different characters)");
            }
            else
            {
                testCode = code;
            }
        }

        TestAdmin? testAdmin = null;
        try
        {
            testAdmin = LoadTestAdmin(Get("TEST_ADMIN_NAME"), Get("TEST_ADMIN_EMAIL"));
            if (testAdmin is not null && !testAccess)
            {
                issues.Add("TEST_ADMIN_NAME/TEST_ADMIN_EMAIL: only allowed with AUTH_MODE=test-access-code");
            }
        }
        catch (ConfigException e)
        {
            issues.AddRange(e.Issues);
        }

        var tenant = Get("ENTRA_TENANT_ID");
        var client = Get("ENTRA_CLIENT_ID");
        if (!testAccess)
        {
            foreach (var (key, value) in new[] { ("ENTRA_TENANT_ID", tenant), ("ENTRA_CLIENT_ID", client), ("ENTRA_CLIENT_SECRET", entraSecret) })
            {
                if (value is null)
                {
                    issues.Add($"{key}: required when AUTH_MODE=entra");
                }
            }
        }
        foreach (var (key, value) in new[] { ("ENTRA_TENANT_ID", tenant), ("ENTRA_CLIENT_ID", client) })
        {
            if (value is not null && !GuidPattern().IsMatch(value))
            {
                issues.Add($"{key}: must be a GUID");
            }
        }

        if (Get("SESSION_SECRET") is not null)
        {
            notices.Add("SESSION_SECRET is not used by the .NET app");
        }

        DbConfig? db = null;
        try
        {
            db = LoadDb(get);
        }
        catch (ConfigException e)
        {
            issues.AddRange(e.Issues);
        }

        if (issues.Count > 0)
        {
            throw new ConfigException(issues);
        }

        return new CrmConfig(
            environment, authMode, testCode, testAdmin, trustProxy, logLevel,
            redirectUri ?? "", baseUrl, secure, live,
            new EntraConfig(tenant ?? "", client ?? "", entraSecret ?? ""),
            idle, absolute, Get("DATA_PROTECTION_KEYS_PATH"), Get("APPLICATIONINSIGHTS_CONNECTION_STRING"),
            notices, db!);
    }

    /// <summary>Validates only the database variables. Used by the migrator commands as well.</summary>
    public static DbConfig LoadDb(IConfiguration configuration) => LoadDb(k => configuration[k]);

    public static DbConfig LoadDb(IReadOnlyDictionary<string, string?> values) => LoadDb(k => values.TryGetValue(k, out var v) ? v : null);

    public static DbConfig LoadDb(Func<string, string?> get)
    {
        string? Get(string k) => string.IsNullOrWhiteSpace(get(k)) ? null : get(k)!.Trim();
        var issues = new List<string>();
        DbAuthMode? mode = Get("DB_AUTH_MODE") switch
        {
            "managed-identity" => DbAuthMode.ManagedIdentity,
            "default-azure" => DbAuthMode.DefaultAzure,
            "sql-password" => DbAuthMode.SqlPassword,
            _ => null,
        };
        if (mode is null)
        {
            issues.Add("DB_AUTH_MODE: required, one of managed-identity, default-azure, sql-password");
        }
        var trust = ParseBool(Get("DB_TRUST_SERVER_CERT"), "DB_TRUST_SERVER_CERT", issues);
        var poolMax = ParseInt(Get("SQL_POOL_MAX"), "SQL_POOL_MAX", 1, 100, 10, issues);
        var connString = get("DB_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connString))
        {
            connString = null;
        }

        DbConfig? result = null;
        if (connString is not null)
        {
            if (mode != DbAuthMode.SqlPassword)
            {
                issues.Add("DB_CONNECTION_STRING: only allowed when DB_AUTH_MODE=sql-password");
            }
            foreach (var k in new[] { "DB_SERVER", "DB_NAME", "DB_USER", "DB_PASSWORD" })
            {
                if (Get(k) is not null)
                {
                    issues.Add($"{k}: must not be set together with DB_CONNECTION_STRING (use one or the other)");
                }
            }
            try
            {
                var c = ConnectionStringParser.Parse(connString);
                result = new DbConfig(c.Server, c.Port, c.Database, DbAuthMode.SqlPassword, c.User, c.Password, trust || c.TrustServerCertificate, poolMax);
            }
            catch (ConnectionStringException e)
            {
                issues.Add($"DB_CONNECTION_STRING: invalid: {e.Message}");
            }
        }
        else
        {
            var server = Get("DB_SERVER");
            var name = Get("DB_NAME");
            var user = Get("DB_USER");
            var password = Get("DB_PASSWORD");
            if (server is null) { issues.Add("DB_SERVER: required (or set DB_CONNECTION_STRING)"); }
            if (name is null) { issues.Add("DB_NAME: required (or set DB_CONNECTION_STRING)"); }
            if (mode == DbAuthMode.SqlPassword)
            {
                if (user is null) { issues.Add("DB_USER: required when DB_AUTH_MODE=sql-password"); }
                if (password is null) { issues.Add("DB_PASSWORD: required when DB_AUTH_MODE=sql-password"); }
            }
            else
            {
                if (user is not null) { issues.Add("DB_USER: only allowed when DB_AUTH_MODE=sql-password"); }
                if (password is not null) { issues.Add("DB_PASSWORD: only allowed when DB_AUTH_MODE=sql-password"); }
            }
            if (server is not null && name is not null && mode is not null)
            {
                result = new DbConfig(server, null, name, mode.Value, user, password, trust, poolMax);
            }
        }
        if (issues.Count > 0 || result is null)
        {
            throw new ConfigException(issues);
        }
        return result;
    }

    /// <summary>Optional named Admin for the interim test site: both variables or neither.</summary>
    public static TestAdmin? LoadTestAdmin(string? nameRaw, string? emailRaw)
    {
        var name = nameRaw?.Trim();
        var email = emailRaw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(email))
        {
            return null;
        }
        var issues = new List<string>();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(email))
        {
            issues.Add("TEST_ADMIN_NAME and TEST_ADMIN_EMAIL must be set together");
        }
        if (!string.IsNullOrEmpty(name) && (name.EnumerateRunes().Count() > 200 || name.Any(char.IsControl)))
        {
            issues.Add("TEST_ADMIN_NAME: 1-200 characters, no control characters");
        }
        if (!string.IsNullOrEmpty(email) && !EmailPattern().IsMatch(email))
        {
            issues.Add("TEST_ADMIN_EMAIL: must look like name@example.com");
        }
        if (issues.Count > 0)
        {
            throw new ConfigException(issues);
        }
        return new TestAdmin(name!, email!, TestUsers.AdminOid(email!));
    }

    private static bool ParseBool(string? value, string key, List<string> issues)
    {
        switch (value)
        {
            case null: return false;
            case "true": return true;
            case "false": return false;
            default:
                issues.Add($"{key}: must be true or false");
                return false;
        }
    }

    private static int ParseInt(string? value, string key, int min, int max, int fallback, List<string> issues)
    {
        if (value is null)
        {
            return fallback;
        }
        if (!IntPattern().IsMatch(value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
        {
            issues.Add($"{key}: must be a whole number between {min} and {max}");
            return fallback;
        }
        return n;
    }

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"^[^\s@]{1,64}@[^\s@]{1,190}\.[^\s@]{2,}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[0-9]{1,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IntPattern();
}
