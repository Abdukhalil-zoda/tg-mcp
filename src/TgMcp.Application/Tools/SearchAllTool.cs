using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>search_all: global search; results from chats without read + search are silently dropped.</summary>
public sealed class SearchAllTool
{
    /// <summary>Hard cap on result pages fetched per call.</summary>
    public const int MaxPages = 5;

    private readonly ToolContext _c;

    /// <summary>Creates the tool.</summary>
    public SearchAllTool(ToolContext context) => _c = context;

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string query, int limit, CancellationToken ct) =>
        _c.Runner.RunAsync("search_all", [Need.Read, Need.Search], async token =>
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is empty", nameof(query));
            var max = _c.ClampLimit(limit);
            var now = _c.Time.GetUtcNow();
            var chats = new Dictionary<long, (TgChat Chat, Decision D)>();
            var kept = new List<TgMessage>();
            var dropped = 0;
            var offset = "";
            for (var page = 0; page < MaxPages && kept.Count < max; page++)
            {
                var res = await _c.Telegram.SearchMessagesAsync(query, offset, MessageCollector.PageSize, token).ConfigureAwait(false);
                foreach (var m in res.Messages)
                {
                    if (!chats.TryGetValue(m.ChatId, out var entry))
                    {
                        var chat = await _c.Telegram.GetChatAsync(m.ChatId, token).ConfigureAwait(false);
                        chats[m.ChatId] = entry = (chat, _c.Policy.Check(chat.Info, Need.Read, Need.Search));
                    }
                    if (entry.D.Allowed && entry.D.DateOk(m.Date, now) && entry.D.TopicOk(m.TopicId)) kept.Add(m);
                    else dropped++;
                    if (kept.Count >= max) break;
                }
                if (string.IsNullOrEmpty(res.NextOffset) || res.Messages.Count == 0) break;
                offset = res.NextOffset;
            }
            return ToolOutcome.Ok(ToolContext.MessagesPayload(null, kept), null, kept.Count,
                new Decision(true, "per-chat", "ok"), $"ok, {dropped} dropped by policy");
        }, ct);
}
