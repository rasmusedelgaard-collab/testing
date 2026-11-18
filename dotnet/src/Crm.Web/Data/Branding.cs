using Dapper;

namespace Crm.Web.Data;

public sealed record BrandingValues(string AppName, string AccentColor, string? LogoSha)
{
    public static readonly BrandingValues Default = new("CRM", "#1D4ED8", null);
}

public sealed record LogoData(string ContentType, byte[] Content, string Sha256);

/// <summary>
/// Public branding cache (singleton): app name, accent colour, logo sha. TTL 30 s; on DB error serve the last value or
/// defaults and retry after 5 s (never wait for a paused database on the sign-in page).
/// </summary>
public class BrandingCache(SystemDb db, TimeProvider time)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ErrorRetry = TimeSpan.FromSeconds(5);

    /// <summary>Immutable snapshot, swapped as one reference: a struct tuple could be read half-written by another request.</summary>
    private sealed record Snapshot(DateTimeOffset At, BrandingValues Value);

    private Snapshot? _cached;
    private LogoData? _logo;

    private sealed record KeyValue(string K, string V);

    public virtual async Task<BrandingValues> GetAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _cached) is { } c && time.GetUtcNow() - c.At < Ttl)
        {
            return c.Value;
        }
        try
        {
            var rows = await db.QueryAsync<KeyValue>("SELECT SettingKey AS K, SettingValue AS V FROM crm.Settings WHERE SettingKey IN ('app.name', 'branding.accentColor')", null, ct, retryTransient: false).ConfigureAwait(false);
            var logo = await db.QueryAsync<string>("SELECT LOWER(CONVERT(char(64), Sha256, 2)) FROM crm.BrandingAssets WHERE AssetKey = 'logo'", null, ct, retryTransient: false).ConfigureAwait(false);
            var map = rows.ToDictionary(r => r.K, r => r.V);
            var value = new BrandingValues(map.GetValueOrDefault("app.name") ?? BrandingValues.Default.AppName, map.GetValueOrDefault("branding.accentColor") ?? BrandingValues.Default.AccentColor, logo.Count > 0 ? logo[0] : null);
            Volatile.Write(ref _cached, new Snapshot(time.GetUtcNow(), value));
            return value;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var fallback = Volatile.Read(ref _cached)?.Value ?? BrandingValues.Default;
            Volatile.Write(ref _cached, new Snapshot(time.GetUtcNow() - Ttl + ErrorRetry, fallback));
            return fallback;
        }
    }

    public virtual async Task<LogoData?> GetLogoAsync(CancellationToken ct)
    {
        var current = await GetAsync(ct).ConfigureAwait(false);
        if (Volatile.Read(ref _logo) is { } l)
        {
            if (current.LogoSha == l.Sha256)
            {
                return l;
            }
            Interlocked.CompareExchange(ref _logo, null, l); // only clear the entry we saw: a newer one may have been stored meanwhile
        }
        try
        {
            var rows = await db.QueryAsync<LogoRow>("SELECT ContentType AS Ct, Content AS C, LOWER(CONVERT(char(64), Sha256, 2)) AS Sha FROM crm.BrandingAssets WHERE AssetKey = 'logo'", null, ct, retryTransient: false).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                var fresh = new LogoData(rows[0].Ct, rows[0].C, rows[0].Sha);
                Volatile.Write(ref _logo, fresh);
                return fresh;
            }
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Called after an Admin saves branding so this instance shows the change immediately.</summary>
    public void Invalidate()
    {
        Volatile.Write(ref _cached, null);
        Volatile.Write(ref _logo, null);
    }

    private sealed record LogoRow(string Ct, byte[] C, string Sha);
}
