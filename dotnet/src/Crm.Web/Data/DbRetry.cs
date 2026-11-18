using Microsoft.Extensions.Logging;

namespace Crm.Web.Data;

/// <summary>
/// Retries work that cannot have committed anything (connection acquisition, the context batch, idempotent system
/// statements) while the database wakes up (overview 8.1). One shared "waking" schedule per instance: concurrent
/// requests wait for the same attempt instead of each escalating its own backoff loop.
/// SqlClient's configurable retry logic is deliberately not used (it would retry commands inside the callback).
/// </summary>
public sealed class DbRetry(TimeProvider time, ILogger<DbRetry> log)
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);
    private static readonly int[] StepsMs = [1000, 2000, 4000, 8000];
    private const int LaterStepMs = 15000;

    private readonly Lock _gate = new();
    private int _attempt;
    private DateTimeOffset _nextAt;
    private DateTimeOffset _startedAt;
    private bool _waking;

    public Func<Exception, bool> IsTransient { get; init; } = Transient.IsTransient;

    public Func<double> Random { get; init; } = System.Random.Shared.NextDouble;

    public static int BackoffMs(int attempt, double random01)
    {
        var baseMs = attempt < StepsMs.Length ? StepsMs[attempt] : LaterStepMs;
        return (int)Math.Round(baseMs * (0.75 + random01 * 0.5), MidpointRounding.AwayFromZero);
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> fn, string label, CancellationToken ct)
    {
        for (; ; )
        {
            try
            {
                var result = await fn().ConfigureAwait(false);
                lock (_gate)
                {
                    _waking = false;
                    _attempt = 0;
                }
                return result;
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var wait = Schedule(ex, label);
                if (wait is null)
                {
                    throw;
                }
                await Task.Delay(wait.Value, time, ct).ConfigureAwait(false);
            }
        }
    }

    public Task RunAsync(Func<Task> fn, string label, CancellationToken ct) =>
        RunAsync<bool>(async () => { await fn().ConfigureAwait(false); return true; }, label, ct);

    private TimeSpan? Schedule(Exception ex, string label)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (!_waking)
            {
                _waking = true;
                _startedAt = now;
                _attempt = 0;
                _nextAt = now;
            }
            if (now >= _nextAt)
            {
                var delay = TimeSpan.FromMilliseconds(BackoffMs(_attempt++, Random()));
                _nextAt = now + delay;
            }
            if (_nextAt - _startedAt > Budget)
            {
                log.LogError("database still unavailable; retry budget exhausted (label {Label}, error {Number}, waited {Elapsed} ms)", label, Transient.SqlNumber(ex), (long)(now - _startedAt).TotalMilliseconds);
                _waking = false;
                return null;
            }
            log.LogWarning("transient database error; retrying (label {Label}, attempt {Attempt}, error {Number})", label, _attempt, Transient.SqlNumber(ex));
            return _nextAt - now;
        }
    }
}
