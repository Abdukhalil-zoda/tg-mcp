namespace TgMcp.Application;

/// <summary>Limits tool calls per process.</summary>
public interface IRateLimiter
{
    /// <summary>Takes one token; false if the caller must be refused.</summary>
    bool TryAcquire();
}

/// <summary>Token bucket: capacity = rate per minute, refilled continuously at rate/60 tokens per second.</summary>
public sealed class TokenBucket : IRateLimiter
{
    private readonly TimeProvider _time;
    private readonly double _capacity;
    private readonly double _perSecond;
    private readonly Lock _lock = new();
    private double _tokens;
    private long _last;

    /// <summary>Creates a full bucket.</summary>
    /// <param name="ratePerMinute">Allowed calls per minute (at least 1).</param>
    /// <param name="time">Clock (use a fake in tests).</param>
    public TokenBucket(int ratePerMinute, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ratePerMinute, 1);
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _capacity = ratePerMinute;
        _perSecond = ratePerMinute / 60.0;
        _tokens = _capacity;
        _last = time.GetTimestamp();
    }

    /// <inheritdoc />
    public bool TryAcquire()
    {
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            _tokens = Math.Min(_capacity, _tokens + (_time.GetElapsedTime(_last, now).TotalSeconds * _perSecond));
            _last = now;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}
