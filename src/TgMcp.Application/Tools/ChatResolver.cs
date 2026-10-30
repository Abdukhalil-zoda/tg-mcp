using System.Globalization;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>Turns the <c>chat</c> tool argument (numeric id or @username) into a chat.</summary>
public sealed class ChatResolver
{
    private readonly ITelegramClient _client;

    /// <summary>Creates the resolver.</summary>
    public ChatResolver(ITelegramClient client) => _client = client;

    /// <summary>Resolves <paramref name="chat"/>; throws <see cref="ArgumentException"/> if it is malformed or unknown.</summary>
    public async Task<TgChat> ResolveAsync(string chat, CancellationToken ct)
    {
        var s = (chat ?? "").Trim();
        if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) && id != 0)
            return await _client.GetChatAsync(id, ct).ConfigureAwait(false);
        var u = Names.NormalizeUsername(s);
        if (u.Length == 0 || !u.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("chat must be a numeric chat id or a @username", nameof(chat));
        return await _client.SearchPublicChatAsync(u, ct).ConfigureAwait(false)
            ?? throw new ArgumentException("no public chat with that username", nameof(chat));
    }
}

/// <summary>Pages through newest-first message sources, applying SinceDays and Topics.</summary>
public static class MessageCollector
{
    /// <summary>Hard cap on pages fetched per call.</summary>
    public const int MaxPages = 20;

    /// <summary>Largest page requested from Telegram.</summary>
    public const int PageSize = 100;

    /// <summary>
    /// Collects up to <paramref name="limit"/> messages from <paramref name="fetch"/>(fromMessageId, pageLimit).
    /// Stops at the first message older than the SinceDays cutoff. Returns kept messages and the number dropped by Topics.
    /// </summary>
    public static async Task<(List<TgMessage> Kept, int Dropped)> CollectAsync(
        Func<long, int, Task<IReadOnlyList<TgMessage>>> fetch, long fromMessageId, int limit, Decision d, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(d);
        var kept = new List<TgMessage>();
        var seen = new HashSet<long>();
        var dropped = 0;
        var from = fromMessageId;
        for (var page = 0; page < MaxPages && kept.Count < limit; page++)
        {
            var batch = await fetch(from, Math.Min(PageSize, limit - kept.Count)).ConfigureAwait(false);
            var fresh = batch.Where(m => seen.Add(m.Id)).ToList();
            if (fresh.Count == 0) break;
            foreach (var m in fresh)
            {
                if (!d.DateOk(m.Date, now)) return (kept, dropped);
                if (!d.TopicOk(m.TopicId)) { dropped++; continue; }
                kept.Add(m);
                if (kept.Count >= limit) break;
            }
            from = fresh.Min(m => m.Id);
        }
        return (kept, dropped);
    }
}
