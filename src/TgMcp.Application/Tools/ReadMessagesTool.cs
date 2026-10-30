using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>read_messages: chat history, newest first. Needs read.</summary>
public sealed class ReadMessagesTool
{
    private readonly ToolContext _c;

    /// <summary>Creates the tool.</summary>
    public ReadMessagesTool(ToolContext context) => _c = context;

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string chat, int limit, long fromMessageId, CancellationToken ct) =>
        _c.Runner.RunAsync("read_messages", [Need.Read], async token =>
        {
            var target = await _c.Chats.ResolveAsync(chat, token).ConfigureAwait(false);
            var d = _c.Policy.Check(target.Info, Need.Read);
            if (!d.Allowed) return ToolOutcome.Denied(d, target.Id);
            var (kept, dropped) = await MessageCollector.CollectAsync(
                (from, n) => _c.Telegram.GetChatHistoryAsync(target.Id, from, n, token),
                fromMessageId, _c.ClampLimit(limit), d, _c.Time.GetUtcNow()).ConfigureAwait(false);
            return ToolOutcome.Ok(ToolContext.MessagesPayload(target, kept), target.Id, kept.Count, d,
                dropped > 0 ? $"ok, {dropped} outside allowed topics" : "ok");
        }, ct);
}
