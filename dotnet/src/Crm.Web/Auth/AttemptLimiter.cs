namespace Crm.Web.Auth;

/// <summary>In-memory fixed-window limiter for FAILED access-code attempts (port of rateLimiter.ts). Per instance.</summary>
public sealed class AttemptLimiter(int maxAttempts, TimeProvider time, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(15);
    private readonly Dictionary<string, (int Count, DateTimeOffset Start)> _entries = [];
    private readonly Lock _lock = new();

    private bool TryCurrent(string key, out (int Count, DateTimeOffset Start) entry)
    {
        if (_entries.TryGetValue(key, out entry))
        {
            if (time.GetUtcNow() - entry.Start >= _window)
            {
                _entries.Remove(key);
                return false;
            }
            return true;
        }
        return false;
    }

    public bool IsLocked(string key)
    {
        lock (_lock)
        {
            return TryCurrent(key, out var e) && e.Count >= maxAttempts;
        }
    }

    public void RecordFailure(string key)
    {
        lock (_lock)
        {
            _entries[key] = TryCurrent(key, out var e) ? (e.Count + 1, e.Start) : (1, time.GetUtcNow());
            if (_entries.Count > 10_000)
            {
                foreach (var k in _entries.Keys.ToList())
                {
                    TryCurrent(k, out _);
                }
            }
        }
    }

    public void Reset(string key)
    {
        lock (_lock)
        {
            _entries.Remove(key);
        }
    }
}
