using System.Globalization;
using System.Text.RegularExpressions;

namespace TgMcp.Application.Telegram;

/// <summary>Timeout and FLOOD_WAIT limits for one Telegram request.</summary>
/// <param name="Timeout">Per-attempt timeout.</param>
/// <param name="MaxAttempts">Maximum attempts including the first.</param>
/// <param name="MaxSingleWait">Longest single flood wait that is honored (default 60 s).</param>
/// <param name="MaxTotalWait">Longest cumulative flood wait per request (default 120 s).</param>
public sealed record RetryOptions(
    TimeSpan Timeout,
    int MaxAttempts = 3,
    TimeSpan? MaxSingleWait = null,
    TimeSpan? MaxTotalWait = null)
{
    /// <summary>Effective single-wait cap.</summary>
    public TimeSpan SingleWaitCap => MaxSingleWait ?? TimeSpan.FromSeconds(60);

    /// <summary>Effective total-wait cap.</summary>
    public TimeSpan TotalWaitCap => MaxTotalWait ?? TimeSpan.FromSeconds(120);
}

/// <summary>Timeout plus bounded FLOOD_WAIT handling that waits exactly as long as the server asked.</summary>
public static partial class Retry
{
    [GeneratedRegex(@"(?:retry after |FLOOD_WAIT_)(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FloodRegex();

    /// <summary>Seconds to wait if <paramref name="e"/> is a flood-wait error, else null.</summary>
    public static int? FloodWaitSeconds(TelegramCallException e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Code != 429 && !e.TdMessage.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase)) return null;
        var m = FloodRegex().Match(e.TdMessage);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    /// <summary>Runs <paramref name="call"/> with a timeout per attempt, retrying flood waits within the caps.</summary>
    /// <exception cref="TelegramCallException">Telegram error, timeout (code 408) or flood wait beyond the caps (429).</exception>
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> call,
        RetryOptions options,
        TimeProvider time,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        var waited = TimeSpan.Zero;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await Attempt(call, options.Timeout, time, ct).ConfigureAwait(false);
            }
            catch (TelegramCallException e) when (FloodWaitSeconds(e) is int s)
            {
                var wait = TimeSpan.FromSeconds(s);
                if (attempt >= options.MaxAttempts || wait > options.SingleWaitCap || waited + wait > options.TotalWaitCap)
                {
                    throw new TelegramCallException(429,
                        $"FLOOD_WAIT {s}s: Telegram asks to slow down; giving up after attempt {attempt} (waited {waited.TotalSeconds:0}s)");
                }
                waited += wait;
                await Task.Delay(wait, time, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<T> Attempt<T>(Func<CancellationToken, Task<T>> call, TimeSpan timeout, TimeProvider time, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            return await call(cts.Token).WaitAsync(timeout, time, cts.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            throw new TelegramCallException(408, $"request timed out after {timeout.TotalSeconds:0}s");
        }
    }
}
