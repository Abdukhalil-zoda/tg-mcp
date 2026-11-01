using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>Limits search_public calls (limits.publicSearchesPerHour).</summary>
public interface IPublicSearchLimiter
{
    /// <summary>Takes one token; false if the caller must be refused.</summary>
    bool TryAcquire();
}

/// <summary>Token bucket of limits.publicSearchesPerHour tokens per hour.</summary>
public sealed class PublicSearchRateLimiter : IPublicSearchLimiter
{
    private readonly TokenBucket _bucket;

    /// <summary>Creates the limiter.</summary>
    public PublicSearchRateLimiter(int perHour, TimeProvider time) => _bucket = new TokenBucket(perHour, TimeSpan.FromHours(1), time);

    /// <inheritdoc />
    public bool TryAcquire() => _bucket.TryAcquire();
}

/// <summary>
/// search_public: public chats, channels and users matching a text (searchPublicChats), with an exact @username match
/// (searchPublicChat) first. Directory data only, no messages. Needs features.searchPublic; chats hit by a deny rule
/// are silently dropped. limits.publicSearchesPerHour applies.
/// </summary>
public sealed class SearchPublicTool
{
    /// <summary>Most results per call.</summary>
    public const int MaxResults = 50;

    private readonly ToolContext _c;
    private readonly IPublicSearchLimiter _limiter;

    /// <summary>Creates the tool.</summary>
    public SearchPublicTool(ToolContext context, IPublicSearchLimiter limiter)
    {
        _c = context;
        _limiter = limiter;
    }

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string query, int limit, CancellationToken ct) =>
        _c.Runner.RunAsync("search_public", [Need.SearchPublic], async token =>
        {
            var q = (query ?? "").Trim();
            if (q.Length == 0) throw new ArgumentException("query is empty", nameof(query));
            if (q.Length > 64) throw new ArgumentException("query is too long (64 characters at most)", nameof(query));
            if (!_limiter.TryAcquire())
                return ToolOutcome.Refused($"public search limit reached (limits.publicSearchesPerHour={_c.Policy.Config.PublicSearchesPerHour})", null, "limits");
            var max = Math.Clamp(limit, 1, MaxResults);
            var found = new List<TgPublicChat>();
            var name = q.TrimStart('@');
            if (JoinTarget.IsUsername(name) && await _c.Telegram.FindPublicChatAsync(name.ToLowerInvariant(), token).ConfigureAwait(false) is { } exact)
                found.Add(exact);
            foreach (var r in await _c.Telegram.SearchPublicChatsAsync(q, max, token).ConfigureAwait(false))
            {
                if (found.All(f => f.Id != r.Id)) found.Add(r);
            }
            var kept = new List<object>();
            var dropped = 0;
            foreach (var r in found)
            {
                if (kept.Count >= max) break;
                if (!_c.Policy.Effective(r.Info).Allowed) { dropped++; continue; }
                kept.Add(new
                {
                    id = r.Id,
                    title = r.Title,
                    username = r.Usernames is [var u, ..] ? "@" + u : null,
                    type = r.Type,
                    member_count = r.Type is "group" or "supergroup" or "channel" ? r.MemberCount : (int?)null,
                });
            }
            var payload = new
            {
                note = ToolResult.ThirdPartyNote,
                results = kept,
                hint = "Finding a chat grants no access: reading needs a rule in \"chats\"; joining uses join_chat.",
            };
            return new ToolOutcome(ToolResult.Ok(payload), null, kept.Count, true, "search_public", $"ok, {dropped} dropped by policy");
        }, ct);
}

/// <summary>
/// find_chats: the owner's own groups and channels (main and archive chat lists) as directory data: id, title, type,
/// @username, member count, archived, whether the owner can add members or include the chat in a folder link.
/// Returns no messages. Needs features.findChats only: it is deliberately NOT limited by per-chat read rules, so the
/// owner can build lists of chats; deny rules still hide chats. Keywords are optional (none = the whole directory).
/// </summary>
public sealed class FindChatsTool
{
    /// <summary>Most chats scanned per call (main + archive).</summary>
    public const int MaxScan = 2000;

    /// <summary>Most results per page.</summary>
    public const int MaxPage = 2000;

    private readonly ToolContext _c;

    /// <summary>Creates the tool.</summary>
    public FindChatsTool(ToolContext context) => _c = context;

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(IReadOnlyList<string>? keywords, string? kind, int offset, int limit, CancellationToken ct) =>
        _c.Runner.RunAsync("find_chats", [Need.FindChats], async token =>
        {
            var words = (keywords ?? []).Select(k => (k ?? "").Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (words.Count > 50) throw new ArgumentException("at most 50 keywords", nameof(keywords));
            var filter = (kind ?? "any").Trim().ToLowerInvariant() switch
            {
                "" or "any" => (ChatKind?)null,
                "group" => ChatKind.Group,
                "channel" => ChatKind.Channel,
                _ => throw new ArgumentException("kind must be group, channel or any", nameof(kind)),
            };
            if (offset < 0) throw new ArgumentException("offset must be >= 0", nameof(offset));
            var page = Math.Clamp(limit, 1, MaxPage);

            var all = await _c.Telegram.ScanGroupsAsync(MaxScan, token).ConfigureAwait(false);
            var denied = 0;
            var matches = new List<TgDirectoryChat>();
            foreach (var chat in all)
            {
                if (filter is ChatKind k && chat.Kind != k) continue;
                if (words.Count > 0 && !words.Any(w => Matches(chat, w))) continue;
                if (!_c.Policy.Effective(chat.Info).Allowed) { denied++; continue; }
                matches.Add(chat);
            }
            var slice = matches.Skip(offset).Take(page).Select(chat => new
            {
                id = chat.Id,
                title = chat.Title,
                type = chat.Type,
                username = chat.Usernames is [var u, ..] ? "@" + u : null,
                member_count = chat.MemberCount,
                archived = chat.Archived,
                can_add_members = chat.CanAddMembers,
                can_include_in_folder_link = chat.CanIncludeInFolderLink,
            }).ToList();
            var next = offset + slice.Count < matches.Count ? offset + slice.Count : (int?)null;
            var payload = new
            {
                note = ToolResult.ThirdPartyNote,
                total = matches.Count,
                offset,
                next_offset = next,
                scanned = all.Count,
                scan_capped = all.Count >= MaxScan,
                chats = slice,
                access = "Directory data only: finding a chat grants no read or write access.",
            };
            return new ToolOutcome(ToolResult.Ok(payload), null, slice.Count, true, "find_chats",
                $"ok, {words.Count} keyword(s), {matches.Count} match(es), {denied} dropped by policy");
        }, ct);

    private static bool Matches(TgDirectoryChat chat, string word)
    {
        var w = word.TrimStart('@');
        return chat.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
            || (w.Length > 0 && chat.Usernames.Any(u => u.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }
}
