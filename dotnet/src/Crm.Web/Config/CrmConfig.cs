namespace Crm.Web.Config;

public enum DbAuthMode { ManagedIdentity, DefaultAzure, SqlPassword }

public enum AuthMode { Entra, TestAccessCode }

public sealed record DbConfig(
    string Server,
    int? Port,
    string Database,
    DbAuthMode AuthMode,
    string? User,
    string? Password,
    bool TrustServerCertificate,
    int PoolMax)
{
    // The password must never appear in logs or exception messages.
    public override string ToString() => $"DbConfig {{ Server = {Server}, Database = {Database}, AuthMode = {AuthMode} }}";
}

public sealed record TestAdmin(string Name, string Email, string Oid);

public sealed record EntraConfig(string TenantId, string ClientId, string ClientSecret)
{
    public override string ToString() => "EntraConfig { ... }";
}

public sealed record CrmConfig(
    string Environment,
    AuthMode AuthMode,
    string? TestAccessCode,
    TestAdmin? TestAdmin,
    int TrustProxy,
    string LogLevel,
    string RedirectUri,
    string BaseUrl,
    bool SecureCookies,
    bool Live,
    EntraConfig Entra,
    int SessionIdleMinutes,
    int SessionAbsoluteHours,
    string? DataProtectionKeysPath,
    string? AppInsightsConnectionString,
    IReadOnlyList<string> Notices,
    DbConfig Db)
{
    public bool IsDevelopment => Environment == "Development";
    public bool IsProduction => Environment == "Production";
    public bool IsTestAccess => AuthMode == AuthMode.TestAccessCode;

    // Never print secrets (access code, Entra secret, DB password).
    public override string ToString() => $"CrmConfig {{ Environment = {Environment}, AuthMode = {AuthMode} }}";
}

public sealed class ConfigException(IReadOnlyList<string> issues)
    : Exception("Invalid configuration:" + System.Environment.NewLine + string.Join(System.Environment.NewLine, issues.Select(i => "  - " + i)))
{
    public IReadOnlyList<string> Issues { get; } = issues;
}
