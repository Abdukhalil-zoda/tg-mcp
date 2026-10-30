using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>search_chat: text search in one chat. Needs read + search.</summary>
public sealed class SearchChatTool
{
    private readonly ToolContext _c;

    /// <summary>Creates the tool.</summary>
    public SearchChatTool(ToolContext context) => _c = context;

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string chat, string query, int limit, long fromMessageId, CancellationToken ct) =>
        _c.Runner.RunAsync("search_chat", [Need.Read, Need.Search], async token =>
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is empty", nameof(query));
            var target = await _c.Chats.ResolveAsync(chat, token).ConfigureAwait(false);
            var d = _c.Policy.Check(target.Info, Need.Read, Need.Search);
            if (!d.Allowed) return _c.Deny(d, target);
            var (kept, dropped) = await MessageCollector.CollectAsync(
                (from, n) => _c.Telegram.SearchChatMessagesAsync(target.Id, query, from, n, token),
                fromMessageId, _c.ClampLimit(limit), d, _c.Time.GetUtcNow()).ConfigureAwait(false);
            return ToolOutcome.Ok(ToolContext.MessagesPayload(target, kept), target.Id, kept.Count, d,
                dropped > 0 ? $"ok, {dropped} outside allowed topics" : "ok");
        }, ct);
}
