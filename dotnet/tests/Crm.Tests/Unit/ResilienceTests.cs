using Crm.Web.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Crm.Tests.Unit;

public class ResilienceTests
{
    private sealed class Boom(string m) : Exception(m);

    [Theory]
    [InlineData(40613)]
    [InlineData(40197)]
    [InlineData(40501)]
    [InlineData(40540)]
    [InlineData(49918)]
    [InlineData(49919)]
    [InlineData(49920)]
    [InlineData(4221)]
    [InlineData(10928)]
    [InlineData(10929)]
    [InlineData(-2)]
    [InlineData(20)]
    [InlineData(64)]
    [InlineData(233)]
    [InlineData(10053)]
    [InlineData(10054)]
    [InlineData(10060)]
    public void Transient_numbers(int n) => Assert.True(Transient.IsTransientNumber(n));

    [Theory]
    [InlineData(33504)]
    [InlineData(547)]
    [InlineData(51000)]
    [InlineData(18456)]
    [InlineData(2627)]
    [InlineData(50011)]
    public void Never_retried_numbers(int n) => Assert.False(Transient.IsTransientNumber(n));

    [Fact]
    public void Plain_exceptions_are_not_transient()
    {
        Assert.False(Transient.IsTransient(new InvalidOperationException("x")));
        Assert.False(Transient.IsTransient(null));
    }

    [Fact]
    public void Backoff_steps_grow_1_2_4_8_then_15_seconds() =>
        Assert.Equal([1000, 2000, 4000, 8000, 15000, 15000], new[] { 0, 1, 2, 3, 4, 9 }.Select(a => DbRetry.BackoffMs(a, 0.5)));

    [Fact]
    public void Backoff_jitter_is_plus_minus_25_percent()
    {
        Assert.Equal(750, DbRetry.BackoffMs(0, 0));
        Assert.Equal(1250, DbRetry.BackoffMs(0, 1));
    }

    private static DbRetry Retry(FakeTimeProvider time) => new(time, NullLogger<DbRetry>.Instance) { IsTransient = e => e is Boom { Message: "transient" }, Random = () => 0.5 };

    [Fact]
    public async Task Retries_transient_errors_with_backoff_then_succeeds()
    {
        var time = new FakeTimeProvider();
        var retry = Retry(time);
        var calls = 0;
        var task = retry.RunAsync(() => ++calls < 3 ? throw new Boom("transient") : Task.FromResult("ok"), "t", CancellationToken.None);
        await Task.Yield();
        for (var i = 0; i < 10 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("ok", await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Does_not_retry_non_transient_errors()
    {
        var calls = 0;
        await Assert.ThrowsAsync<Boom>(() => Retry(new FakeTimeProvider()).RunAsync<int>(() => { calls++; throw new Boom("other"); }, "t", CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Gives_up_when_the_wait_budget_is_spent()
    {
        var time = new FakeTimeProvider();
        var retry = Retry(time);
        var calls = 0;
        var task = retry.RunAsync<int>(() => { calls++; throw new Boom("transient"); }, "t", CancellationToken.None);
        var waited = TimeSpan.Zero;
        while (!task.IsCompleted && waited < TimeSpan.FromMinutes(5))
        {
            await Task.Delay(2);
            time.Advance(TimeSpan.FromSeconds(1));
            waited += TimeSpan.FromSeconds(1);
        }
        await Assert.ThrowsAsync<Boom>(() => task);
        Assert.InRange(waited.TotalSeconds, 30, 70);
        Assert.True(calls > 3);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_waking_schedule()
    {
        var time = new FakeTimeProvider();
        var retry = Retry(time);
        var attempts = new int[2];
        Task<string> Start(int i) => retry.RunAsync(() => ++attempts[i] < 2 ? throw new Boom("transient") : Task.FromResult("ok"), "t", CancellationToken.None);
        var a = Start(0);
        var b = Start(1);
        await Task.Delay(20);
        time.Advance(TimeSpan.FromSeconds(1.1));
        Assert.Equal("ok", await a.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("ok", await b.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([2, 2], attempts); // no escalation: both waited for the same first step
    }
}
