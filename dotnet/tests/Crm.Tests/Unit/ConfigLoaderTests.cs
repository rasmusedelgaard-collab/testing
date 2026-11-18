using Crm.Web.Config;
using Crm.Web.Services;

namespace Crm.Tests.Unit;

public class ConfigLoaderTests
{
    private const string Guid1 = "11111111-2222-4333-8444-555555555555";
    private const string Code = "a-long-shared-code-1234-xyz";
    private const string Pipe = "https://crm.example.com/auth/redirect";

    private static Dictionary<string, string?> Entra() => new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Production",
        ["ENTRA_TENANT_ID"] = Guid1,
        ["ENTRA_CLIENT_ID"] = Guid1,
        ["ENTRA_CLIENT_SECRET"] = "s3cret",
        ["REDIRECT_URI"] = Pipe,
        ["DB_SERVER"] = "sql.example.com",
        ["DB_NAME"] = "crm",
        ["DB_AUTH_MODE"] = "managed-identity",
    };

    private static Dictionary<string, string?> TestMode() => new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Production",
        ["AUTH_MODE"] = "test-access-code",
        ["REDIRECT_URI"] = "https://crmtest.example.com/auth/redirect",
        ["TEST_ACCESS_CODE"] = Code,
        ["DB_SERVER"] = "db.example.com",
        ["DB_NAME"] = "db1",
        ["DB_AUTH_MODE"] = "sql-password",
        ["DB_USER"] = "owner",
        ["DB_PASSWORD"] = "pw",
    };

    private static string Issues(Dictionary<string, string?> env)
    {
        var e = Assert.Throws<ConfigException>(() => CrmConfigLoader.Load(env));
        return e.Message;
    }

    private static Dictionary<string, string?> With(Dictionary<string, string?> d, params (string, string?)[] extra)
    {
        foreach (var (k, v) in extra) { d[k] = v; }
        return d;
    }

    private static Dictionary<string, string?> Without(Dictionary<string, string?> d, string key)
    {
        d.Remove(key);
        return d;
    }

    [Fact]
    public void Accepts_a_complete_production_entra_config()
    {
        var c = CrmConfigLoader.Load(Entra());
        Assert.Equal(AuthMode.Entra, c.AuthMode);
        Assert.True(c.SecureCookies);
        Assert.Equal("https://crm.example.com", c.BaseUrl);
        Assert.Equal(DbAuthMode.ManagedIdentity, c.Db.AuthMode);
        Assert.Equal(60, c.SessionIdleMinutes);
        Assert.Equal(10, c.SessionAbsoluteHours);
    }

    [Theory]
    [InlineData("ENTRA_TENANT_ID")]
    [InlineData("ENTRA_CLIENT_ID")]
    [InlineData("ENTRA_CLIENT_SECRET")]
    [InlineData("REDIRECT_URI")]
    [InlineData("DB_SERVER")]
    [InlineData("DB_NAME")]
    [InlineData("DB_AUTH_MODE")]
    public void Requires(string key) => Assert.Contains(key, Issues(Without(Entra(), key)), StringComparison.Ordinal);

    [Fact]
    public void Rejects_non_https_redirect_in_production_and_wrong_path()
    {
        Assert.Contains("https", Issues(With(Entra(), ("REDIRECT_URI", "http://crm.example.com/auth/redirect"))), StringComparison.Ordinal);
        Assert.Contains("/auth/redirect", Issues(With(Entra(), ("REDIRECT_URI", "https://crm.example.com/signin-oidc"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_non_guid_tenant() => Assert.Contains("ENTRA_TENANT_ID: must be a GUID", Issues(With(Entra(), ("ENTRA_TENANT_ID", "nope"))), StringComparison.Ordinal);

    [Fact]
    public void Db_sql_password_needs_user_and_password()
    {
        var m = Issues(With(Entra(), ("DB_AUTH_MODE", "sql-password")));
        Assert.Contains("DB_USER", m, StringComparison.Ordinal);
        Assert.Contains("DB_PASSWORD", m, StringComparison.Ordinal);
        Assert.Equal("u", CrmConfigLoader.Load(With(Entra(), ("DB_AUTH_MODE", "sql-password"), ("DB_USER", "u"), ("DB_PASSWORD", "p"))).Db.User);
    }

    [Fact]
    public void Db_user_is_rejected_for_other_modes()
    {
        Assert.Contains("only allowed when DB_AUTH_MODE=sql-password", Issues(With(Entra(), ("DB_USER", "u"))), StringComparison.Ordinal);
        Assert.Contains("DB_PASSWORD", Issues(With(Entra(), ("DB_AUTH_MODE", "default-azure"), ("DB_PASSWORD", "p"))), StringComparison.Ordinal);
        Assert.Contains("DB_AUTH_MODE", Issues(With(Entra(), ("DB_AUTH_MODE", "kerberos"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Test_access_code_mode_does_not_need_entra_variables()
    {
        var c = CrmConfigLoader.Load(TestMode());
        Assert.Equal(AuthMode.TestAccessCode, c.AuthMode);
        Assert.Equal(Code, c.TestAccessCode);
        Assert.True(c.SecureCookies);
    }

    [Fact]
    public void Entra_mode_ignores_the_access_code() => Assert.Null(CrmConfigLoader.Load(With(Entra(), ("TEST_ACCESS_CODE", Code))).TestAccessCode);

    [Fact]
    public void Test_access_code_still_needs_database_settings() => Assert.Contains("DB_SERVER", Issues(Without(TestMode(), "DB_SERVER")), StringComparison.Ordinal);

    [Fact]
    public void Test_access_code_works_on_a_non_azure_host_even_with_website_site_name()
    {
        Assert.Equal(AuthMode.TestAccessCode, CrmConfigLoader.Load(With(TestMode(), ("WEBSITE_SITE_NAME", "x"))).AuthMode);
    }

    [Theory]
    [InlineData("LIVE", "true")]
    [InlineData("WEBSITE_INSTANCE_ID", "abc")]
    public void Refuses_test_access_code_when_live(string key, string value)
    {
        Assert.Contains("test-access-code is refused", Issues(With(TestMode(), (key, value))), StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_test_access_code_with_entra_secret() => Assert.Contains("ENTRA_CLIENT_SECRET", Issues(With(TestMode(), ("ENTRA_CLIENT_SECRET", "s"))), StringComparison.Ordinal);

    [Theory]
    [InlineData("LIVE", "true")]
    [InlineData("WEBSITE_INSTANCE_ID", "abc")]
    public void Refuses_sql_password_when_live(string key, string value)
    {
        var env = With(Entra(), ("DB_AUTH_MODE", "sql-password"), ("DB_USER", "u"), ("DB_PASSWORD", "p"), (key, value));
        Assert.Contains("sql-password is refused", Issues(env), StringComparison.Ordinal);
    }

    [Fact]
    public void Allows_managed_identity_when_live() => Assert.True(CrmConfigLoader.Load(With(Entra(), ("LIVE", "true"))).Live);

    [Fact]
    public void Refuses_sql_password_via_connection_string_when_live()
    {
        var env = With(Entra(), ("DB_AUTH_MODE", "sql-password"), ("DB_CONNECTION_STRING", "Server=h;Database=d;User Id=u;Password=p"), ("LIVE", "true"));
        Assert.Contains("sql-password is refused", Issues(Without(Without(env, "DB_SERVER"), "DB_NAME")), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_requires_https_redirect_uri()
    {
        Assert.Contains("REDIRECT_URI", Issues(Without(TestMode(), "REDIRECT_URI")), StringComparison.Ordinal);
        Assert.Contains("https", Issues(With(TestMode(), ("REDIRECT_URI", "http://x.example.com/auth/redirect"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Development_test_mode_may_omit_redirect_uri()
    {
        var c = CrmConfigLoader.Load(Without(With(TestMode(), ("ASPNETCORE_ENVIRONMENT", "Development")), "REDIRECT_URI"));
        Assert.False(c.SecureCookies);
        Assert.Equal("", c.BaseUrl);
    }

    [Fact]
    public void Rejects_repetitive_or_short_codes()
    {
        Assert.Contains("repetitive", Issues(With(TestMode(), ("TEST_ACCESS_CODE", "abababababababababababab"))), StringComparison.Ordinal);
        Assert.Contains("24", Issues(With(TestMode(), ("TEST_ACCESS_CODE", "short-but-varied-123"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Demo_mode_is_refused()
    {
        var e = Assert.Throws<ConfigException>(() => CrmConfigLoader.Load(With(Entra(), ("DEMO_MODE", "true"))));
        Assert.Contains("DEMO_MODE is not supported", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LIVE", "true")]
    [InlineData("WEBSITE_SITE_NAME", "x")]
    [InlineData("WEBSITE_INSTANCE_ID", "x")]
    public void Testing_environment_is_refused_on_real_hosts(string key, string value)
    {
        var env = With(TestMode(), ("ASPNETCORE_ENVIRONMENT", "Testing"), (key, value));
        Assert.Throws<ConfigException>(() => CrmConfigLoader.Load(env));
    }

    [Theory]
    [InlineData("LIVE", "true")]
    [InlineData("WEBSITE_SITE_NAME", "x")]
    [InlineData("WEBSITE_INSTANCE_ID", "x")]
    public void Development_environment_is_refused_on_real_hosts(string key, string value)
    {
        var env = With(Entra(), ("ASPNETCORE_ENVIRONMENT", "Development"), (key, value));
        var e = Assert.Throws<ConfigException>(() => CrmConfigLoader.Load(env));
        Assert.Contains("Development is refused", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Testing_environment_is_allowed_on_a_plain_host() =>
        Assert.Equal("Testing", CrmConfigLoader.Load(With(TestMode(), ("ASPNETCORE_ENVIRONMENT", "Testing"), ("REDIRECT_URI", "http://localhost/auth/redirect"))).Environment);

    [Fact]
    public void Session_secret_only_produces_a_notice()
    {
        var c = CrmConfigLoader.Load(With(Entra(), ("SESSION_SECRET", "whatever")));
        Assert.Contains("SESSION_SECRET is not used by the .NET app", c.Notices);
    }

    [Fact]
    public void Trust_proxy_defaults_and_overrides()
    {
        Assert.Equal(0, CrmConfigLoader.Load(Entra()).TrustProxy); // M1: never trust forwarded headers unless TRUST_PROXY is explicit
        Assert.Equal(1, CrmConfigLoader.Load(With(Entra(), ("TRUST_PROXY", "1"))).TrustProxy);
        Assert.Equal(0, CrmConfigLoader.Load(With(Entra(), ("TRUST_PROXY", "0"))).TrustProxy);
        Assert.Equal(0, CrmConfigLoader.Load(With(Entra(), ("APP_POOL_ID", "x"))).TrustProxy);
        Assert.Equal(0, CrmConfigLoader.Load(With(Entra(), ("ASPNETCORE_ENVIRONMENT", "Development"), ("REDIRECT_URI", "http://localhost:5000/auth/redirect"))).TrustProxy);
        Assert.Contains("TRUST_PROXY", Issues(With(Entra(), ("TRUST_PROXY", "9"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Session_limits_are_validated()
    {
        Assert.Contains("SESSION_IDLE_MINUTES", Issues(With(Entra(), ("SESSION_IDLE_MINUTES", "0"))), StringComparison.Ordinal);
        Assert.Contains("SESSION_ABSOLUTE_HOURS", Issues(With(Entra(), ("SESSION_ABSOLUTE_HOURS", "169"))), StringComparison.Ordinal);
        Assert.Contains("SQL_POOL_MAX", Issues(With(Entra(), ("SQL_POOL_MAX", "x"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Messages_never_contain_values()
    {
        var m = Issues(With(TestMode(), ("TEST_ACCESS_CODE", "short-code-SECRETVALUE")));
        Assert.DoesNotContain("SECRETVALUE", m, StringComparison.Ordinal);
    }

    // ---- DB_CONNECTION_STRING
    private const string Conn = "Server=db.example.com;Database=crm1;User Id=appuser;Password=s3cr3t-pw;";

    private static Dictionary<string, string?> ConnMode() => new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Production",
        ["AUTH_MODE"] = "test-access-code",
        ["REDIRECT_URI"] = "https://t.example.com/auth/redirect",
        ["TEST_ACCESS_CODE"] = Code,
        ["DB_AUTH_MODE"] = "sql-password",
    };

    [Fact]
    public void Connection_string_replaces_the_split_variables()
    {
        var c = CrmConfigLoader.Load(With(ConnMode(), ("DB_CONNECTION_STRING", Conn)));
        Assert.Equal("db.example.com", c.Db.Server);
        Assert.Equal("crm1", c.Db.Database);
        Assert.Equal("appuser", c.Db.User);
        Assert.Equal("s3cr3t-pw", c.Db.Password);
        Assert.Equal("crm1", CrmConfigLoader.LoadDb(new Dictionary<string, string?> { ["DB_AUTH_MODE"] = "sql-password", ["DB_CONNECTION_STRING"] = Conn }).Database);
    }

    [Theory]
    [InlineData("DB_SERVER")]
    [InlineData("DB_NAME")]
    [InlineData("DB_USER")]
    [InlineData("DB_PASSWORD")]
    public void Connection_string_and_split_variables_together_is_an_error(string key)
    {
        Assert.Contains("must not be set together with DB_CONNECTION_STRING", Issues(With(ConnMode(), ("DB_CONNECTION_STRING", Conn), (key, "x"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_string_is_only_for_sql_password() =>
        Assert.Contains("only allowed when DB_AUTH_MODE=sql-password", Issues(With(ConnMode(), ("DB_AUTH_MODE", "managed-identity"), ("DB_CONNECTION_STRING", Conn))), StringComparison.Ordinal);

    [Fact]
    public void Bad_connection_string_is_reported_without_the_password()
    {
        var m = Issues(With(ConnMode(), ("DB_CONNECTION_STRING", "Server=h;Database=d;User Id=u;Password=LEAKME;Bogus=1")));
        Assert.Contains("DB_CONNECTION_STRING: invalid: unknown key \"Bogus\"", m, StringComparison.Ordinal);
        Assert.DoesNotContain("LEAKME", m, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_to_string_never_prints_secrets()
    {
        var c = CrmConfigLoader.Load(With(ConnMode(), ("DB_CONNECTION_STRING", Conn)));
        Assert.DoesNotContain("s3cr3t-pw", c.ToString() + c.Db, StringComparison.Ordinal);
        Assert.DoesNotContain(Code, c.ToString(), StringComparison.Ordinal);
    }

    // ---- Named test admin
    [Fact]
    public void Named_admin_needs_both_and_gets_a_deterministic_oid()
    {
        Assert.Null(CrmConfigLoader.Load(TestMode()).TestAdmin);
        var a = CrmConfigLoader.Load(With(TestMode(), ("TEST_ADMIN_NAME", "Test Person"), ("TEST_ADMIN_EMAIL", "Person@Example.com"))).TestAdmin!;
        Assert.Equal("Test Person", a.Name);
        Assert.Equal("person@example.com", a.Email);
        Assert.Equal("00000000-0000-4000-8000-52468642bfbd", a.Oid); // golden value from the Node function
        Assert.Equal("00000000-0000-4000-8000-ee94bca496e8", TestUsers.AdminOid(" A@B.CO "));
        Assert.DoesNotContain(a.Oid, TestUsers.SeedOids);
        Assert.Contains("set together", Issues(With(TestMode(), ("TEST_ADMIN_NAME", "x"))), StringComparison.Ordinal);
        Assert.Contains("set together", Issues(With(TestMode(), ("TEST_ADMIN_EMAIL", "a@b.co"))), StringComparison.Ordinal);
        Assert.Contains("TEST_ADMIN_EMAIL", Issues(With(TestMode(), ("TEST_ADMIN_NAME", "x"), ("TEST_ADMIN_EMAIL", "not-an-email"))), StringComparison.Ordinal);
        Assert.Contains("TEST_ADMIN_NAME", Issues(With(TestMode(), ("TEST_ADMIN_NAME", new string('x', 201)), ("TEST_ADMIN_EMAIL", "a@b.co"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Named_admin_is_refused_outside_test_access_mode() =>
        Assert.Contains("only allowed with AUTH_MODE=test-access-code", Issues(With(Entra(), ("TEST_ADMIN_NAME", "x"), ("TEST_ADMIN_EMAIL", "a@b.co"))), StringComparison.Ordinal);
}
