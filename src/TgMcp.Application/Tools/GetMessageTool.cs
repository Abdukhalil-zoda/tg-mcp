using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>get_message: one message by id. Needs read; SinceDays and Topics apply.</summary>
public sealed class GetMessageTool
{
    private readonly ToolContext _c;

    /// <summary>Creates the tool.</summary>
    public GetMessageTool(ToolContext context) => _c = context;

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string chat, long messageId, CancellationToken ct) =>
        _c.Runner.RunAsync("get_message", [Need.Read], async token =>
        {
            var target = await _c.Chats.ResolveAsync(chat, token).ConfigureAwait(false);
            var d = _c.Policy.Check(target.Info, Need.Read);
            if (!d.Allowed) return ToolOutcome.Denied(d, target.Id);
            var m = await _c.Telegram.GetMessageAsync(target.Id, messageId, token).ConfigureAwait(false);
            if (m is null) return ToolOutcome.Failed("message not found", target.Id, d.Rule);
            if (!d.DateOk(m.Date, _c.Time.GetUtcNow())) return ToolOutcome.Denied(d.Deny("older than SinceDays"), target.Id);
            if (!d.TopicOk(m.TopicId)) return ToolOutcome.Denied(d.Deny("message is outside allowed topics"), target.Id);
            return ToolOutcome.Ok(ToolContext.MessagesPayload(target, [m]), target.Id, 1, d);
        }, ct);
}
