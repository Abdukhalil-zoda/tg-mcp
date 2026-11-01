namespace TgMcp.Application;

/// <summary>Which pace limit refused a call.</summary>
public enum PaceLimit
{
    /// <summary>Not refused: a token was taken from both buckets.</summary>
    None,
    /// <summary>The per-hour limit is used up.</summary>
    Hour,
    /// <summary>The per-day limit is used up.</summary>
    Day,
}

/// <summary>
/// An hourly and a daily token bucket that are taken together: a call counts against both or neither, so a refusal
/// by the daily limit does not burn an hourly token.
/// </summary>
public class PacedLimiter
{
    private readonly TokenBucket _hour;
    private readonly TokenBucket _day;
    private readonly Lock _lock = new();

    /// <summary>Creates the limiter with full buckets.</summary>
    public PacedLimiter(int perHour, int perDay, TimeProvider time)
    {
        _hour = new TokenBucket(perHour, TimeSpan.FromHours(1), time);
        _day = new TokenBucket(perDay, TimeSpan.FromDays(1), time);
    }

    /// <summary>Takes one token from both buckets, or returns the limit that refused the call.</summary>
    public PaceLimit TryAcquire()
    {
        lock (_lock)
        {
            if (!_day.CanAcquire()) return PaceLimit.Day;
            if (!_hour.CanAcquire()) return PaceLimit.Hour;
            _hour.TryAcquire();
            _day.TryAcquire();
            return PaceLimit.None;
        }
    }
}
