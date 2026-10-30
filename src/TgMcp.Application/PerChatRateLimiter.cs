using System.Collections.Concurrent;

namespace TgMcp.Application;

/// <summary>Limits sends per chat.</summary>
public interface ISendLimiter
{
    /// <summary>Takes one send token for <paramref name="chatId"/>; false if the caller must be refused.</summary>
    bool TryAcquire(long chatId);
}

/// <summary>One token bucket per chat (limits.sendsPerMinute).</summary>
public sealed class PerChatRateLimiter : ISendLimiter
{
    private readonly int _perMinute;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<long, TokenBucket> _buckets = new();

    /// <summary>Creates the limiter.</summary>
    public PerChatRateLimiter(int perMinute, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perMinute, 1);
        _perMinute = perMinute;
        _time = time;
    }

    /// <inheritdoc />
    public bool TryAcquire(long chatId) => _buckets.GetOrAdd(chatId, _ => new TokenBucket(_perMinute, _time)).TryAcquire();
}
