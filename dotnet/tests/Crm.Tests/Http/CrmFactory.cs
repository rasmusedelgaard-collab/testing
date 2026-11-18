using Microsoft.AspNetCore.Hosting;
using Crm.Web.Config;
using Crm.Web.Data;
using Crm.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Crm.Tests.Http;

/// <summary>Branding without a database, for HTTP tests that must run without SQL.</summary>
public sealed class FakeBranding : BrandingCache
{
    public FakeBranding() : base(null!, TimeProvider.System)
    {
    }

    public override Task<BrandingValues> GetAsync(CancellationToken ct) => Task.FromResult(BrandingValues.Default);

    public override Task<LogoData?> GetLogoAsync(CancellationToken ct) => Task.FromResult<LogoData?>(null);
}

public sealed class CrmFactory : WebApplicationFactory<Program>
{
    public const string AccessCode = "test-access-code-abcdef-123456";

    private readonly Dictionary<string, string?> _settings;
    private readonly bool _fakeBranding;

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public CrmFactory() : this(null, null)
    {
    }

    public CrmFactory(DbConfig? db, Dictionary<string, string?>? extra = null)
    {
        _fakeBranding = db is null;
        var server = db is null ? "127.0.0.1,1" : (db.Port is { } p ? $"{db.Server},{p}" : db.Server);
        _settings = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",
            ["AUTH_MODE"] = "test-access-code",
            ["TEST_ACCESS_CODE"] = AccessCode,
            ["DB_AUTH_MODE"] = "sql-password",
            ["DB_CONNECTION_STRING"] = $"Server={server};Database={db?.Database ?? "none"};User Id={db?.User ?? "u"};Password={db?.Password ?? "p"};TrustServerCertificate=True",
            ["DATA_PROTECTION_KEYS_PATH"] = Path.Combine(Path.GetTempPath(), "crm-test-keys-" + Guid.NewGuid().ToString("N")),
        };
        if (extra is not null)
        {
            foreach (var (k, v) in extra) { _settings[k] = v; }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (k, v) in _settings)
        {
            if (v is not null) { builder.UseSetting(k, v); }
        }
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            if (_fakeBranding)
            {
                services.AddSingleton<BrandingCache, FakeBranding>();
            }
        });
    }
}
