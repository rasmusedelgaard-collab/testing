using Crm.Web.Data;

namespace Crm.Web.Infrastructure;

/// <summary>Self-checks at startup (time zone lookup) and the cached /readyz probe.</summary>
public sealed class ReadinessProbe(SystemDb db, TimeProvider time)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);
    private (DateTimeOffset At, bool Ok)? _last;
    private Task<bool>? _inflight;

    public bool StartupChecksPassed { get; set; } = true;

    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        if (!StartupChecksPassed)
        {
            return false;
        }
        if (_last is { } l && time.GetUtcNow() - l.At < Ttl)
        {
            return l.Ok;
        }
        var task = _inflight ??= RunAsync();
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> RunAsync()
    {
        try
        {
            var ok = await db.PingAsync(CancellationToken.None).ConfigureAwait(false);
            _last = (time.GetUtcNow(), ok);
            return ok;
        }
        finally
        {
            _inflight = null;
        }
    }

    public static bool TimeZoneAvailable(string id)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
