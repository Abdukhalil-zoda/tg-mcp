using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>
/// mark_read: marks a chat as read (up to a message, or everything incl. mentions/reactions). This changes the
/// owner's visible state: unread counters and the senders' read receipts. Needs read + mark_read; refused for
/// topic-restricted rules because it would also mark other topics as read.
/// </summary>
public sealed class MarkReadTool
{
    private readonly ToolContext _c;

    /// <summary>Creates the tool.</summary>
    public MarkReadTool(ToolContext context) => _c = context;

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string chat, long upToMessageId, CancellationToken ct) =>
        _c.Runner.RunAsync("mark_read", [Need.Read, Need.MarkRead], async token =>
        {
            if (upToMessageId < 0) throw new ArgumentException("up_to_message_id must not be negative", nameof(upToMessageId));
            var target = await _c.Chats.ResolveAsync(chat, token).ConfigureAwait(false);
            var d = _c.Policy.Check(target.Info, Need.Read, Need.MarkRead);
            if (!d.Allowed) return _c.Deny(d, target);
            if (d.Topics is not null)
            {
                return ToolOutcome.Denied(d.Deny("topic-restricted rule"), target.Id,
                    $"{Denials.Describe(target)} is limited to some topics, and marking it read would also mark other topics as read; mark_read is refused for topic-restricted rules.");
            }
            long? upTo = upToMessageId > 0 ? upToMessageId : null;
            if (upTo is long id && await _c.Telegram.GetMessageAsync(target.Id, id, token).ConfigureAwait(false) is null)
                return ToolOutcome.Failed("up_to_message_id not found", target.Id, d.Rule);
            var marked = await _c.Telegram.MarkReadAsync(target.Id, upTo, token).ConfigureAwait(false);
            var payload = new
            {
                status = marked is null ? "nothing_to_read" : "read",
                chat = target.Id,
                up_to_message_id = marked,
                mentions_and_reactions_cleared = upTo is null,
            };
            return ToolOutcome.Ok(payload, target.Id, marked is null ? 0 : 1, d,
                marked is null ? "empty chat" : $"marked read up to {marked}{(upTo is null ? ", mentions and reactions cleared" : "")}");
        }, ct);
}
