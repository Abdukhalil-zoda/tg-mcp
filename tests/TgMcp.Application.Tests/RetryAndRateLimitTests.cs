using Microsoft.Extensions.Time.Testing;
using TgMcp.Application.Telegram;

namespace TgMcp.Application.Tests;

public class RetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RetryOptions Opts = new(TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData(429, "Too Many Requests: retry after 15", 15)]
    [InlineData(420, "FLOOD_WAIT_7", 7)]
    [InlineData(400, "CHAT_NOT_FOUND", null)]
    [InlineData(429, "Too Many Requests", null)]
    public void Parses_flood_wait(int code, string message, int? expected) =>
        Assert.Equal(expected, Retry.FloodWaitSeconds(new TelegramCallException(code, message)));

    [Fact]
    public async Task Waits_the_requested_seconds_then_retries()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;
        var task = Retry.RunAsync(_ =>
        {
            attempts++;
            return attempts == 1 ? throw new TelegramCallException(429, "Too Many Requests: retry after 10") : Task.FromResult(42);
        }, Opts, time, Ct);
        await Task.Yield();
        Assert.False(task.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(task.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(42, await task);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Gives_up_on_long_flood_wait()
    {
        var attempts = 0;
        var e = await Assert.ThrowsAsync<TelegramCallException>(() => Retry.RunAsync<int>(_ =>
        {
            attempts++;
            throw new TelegramCallException(429, "Too Many Requests: retry after 3600");
        }, Opts, new FakeTimeProvider(), Ct));
        Assert.Equal(429, e.Code);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Bounded_number_of_attempts()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;
        var task = Retry.RunAsync<int>(_ =>
        {
            attempts++;
            throw new TelegramCallException(429, "Too Many Requests: retry after 1");
        }, Opts with { MaxAttempts = 3 }, time, Ct);
        for (var i = 0; i < 5 && !task.IsCompleted; i++)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromSeconds(1));
        }
        await Assert.ThrowsAsync<TelegramCallException>(() => task);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Other_errors_are_not_retried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<TelegramCallException>(() => Retry.RunAsync<int>(_ =>
        {
            attempts++;
            throw new TelegramCallException(400, "CHAT_NOT_FOUND");
        }, Opts, new FakeTimeProvider(), Ct));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Times_out()
    {
        var time = new FakeTimeProvider();
        var never = new TaskCompletionSource<int>();
        var task = Retry.RunAsync(_ => never.Task, Opts, time, Ct);
        time.Advance(TimeSpan.FromSeconds(31));
        var e = await Assert.ThrowsAsync<TelegramCallException>(() => task);
        Assert.Equal(408, e.Code);
    }
}

public class TokenBucketTests
{
    [Fact]
    public void Allows_capacity_then_refills_over_time()
    {
        var time = new FakeTimeProvider();
        var b = new TokenBucket(3, time);
        Assert.True(b.TryAcquire());
        Assert.True(b.TryAcquire());
        Assert.True(b.TryAcquire());
        Assert.False(b.TryAcquire());
        time.Advance(TimeSpan.FromSeconds(20)); // 3/min => 1 token per 20 s
        Assert.True(b.TryAcquire());
        Assert.False(b.TryAcquire());
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(b.TryAcquire());
        Assert.True(b.TryAcquire());
        Assert.True(b.TryAcquire());
        Assert.False(b.TryAcquire()); // never above capacity
    }
}
