using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>
/// edit_message: replaces the text of a message this server sent (sent-log) as the owner. Needs a write mode and
/// "edit_own" (features.editOwn + features.send): "on" edits at once, "draft" saves an edit draft.
/// </summary>
public sealed class EditMessageTool
{
    private readonly ToolContext _c;
    private readonly IDraftStore _drafts;

    /// <summary>Creates the tool.</summary>
    public EditMessageTool(ToolContext context, IDraftStore drafts)
    {
        _c = context;
        _drafts = drafts;
    }

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string chat, long messageId, string text, CancellationToken ct) =>
        _c.Runner.RunAsync("edit_message", [Need.Write, Need.EditOwn], async token =>
        {
            SendMessageTool.ValidateText(text);
            var target = await _c.Chats.ResolveAsync(chat, token).ConfigureAwait(false);
            var edit = new PendingEdit(target.Id, messageId, text);
            var gate = await OwnMessageGate.CheckEditAsync(_c, target, edit, token).ConfigureAwait(false);
            if (gate.Denied is not null) return gate.Denied;
            if (gate.Decision!.Write == WriteMode.Draft)
            {
                var draft = await _drafts.SaveEditAsync(target.Title, edit, token).ConfigureAwait(false);
                return ToolOutcome.Ok(new
                {
                    status = "draft",
                    draft_id = draft.Id,
                    chat = target.Id,
                    message_id = messageId,
                    message = $"edit saved as draft {draft.Id}; the owner applies it with `tgmcp approve {draft.Id}` (nothing was changed)",
                }, target.Id, 1, gate.Decision, $"edit draft {draft.Id} saved for message {messageId}, length {text.Length}");
            }
            await _c.Telegram.EditMessageTextAsync(target.Id, messageId, text, token).ConfigureAwait(false);
            return ToolOutcome.Ok(new { status = "edited", chat = target.Id, message_id = messageId }, target.Id, 1, gate.Decision,
                $"edited message {messageId}, length {text.Length}");
        }, ct);
}

/// <summary>
/// delete_message: deletes messages this server sent (sent-log). Needs a write mode and "delete_own"
/// (features.deleteOwn + features.send). Every deletion is a draft for <c>tgmcp approve</c> unless the chat has
/// "write": "on" AND "delete": { "mode": "on" }. <c>for_everyone</c> revokes it for all users (explicit flag).
/// </summary>
public sealed class DeleteMessageTool
{
    /// <summary>Most messages per call.</summary>
    public const int MaxMessages = 20;

    private readonly ToolContext _c;
    private readonly IDraftStore _drafts;

    /// <summary>Creates the tool.</summary>
    public DeleteMessageTool(ToolContext context, IDraftStore drafts)
    {
        _c = context;
        _drafts = drafts;
    }

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string chat, IReadOnlyList<long> messageIds, bool forEveryone, CancellationToken ct) =>
        _c.Runner.RunAsync("delete_message", [Need.Write, Need.DeleteOwn], async token =>
        {
            var ids = (messageIds ?? []).Distinct().ToList();
            if (ids.Count == 0 || ids.Any(i => i <= 0)) throw new ArgumentException("message_ids must be positive message ids", nameof(messageIds));
            if (ids.Count > MaxMessages) throw new ArgumentException($"at most {MaxMessages} messages per call", nameof(messageIds));
            var target = await _c.Chats.ResolveAsync(chat, token).ConfigureAwait(false);
            var delete = new PendingDelete(target.Id, ids, forEveryone);
            var gate = await OwnMessageGate.CheckDeleteAsync(_c, target, delete, token).ConfigureAwait(false);
            if (gate.Denied is not null) return gate.Denied;
            var immediate = gate.Decision!.Write == WriteMode.On && _c.Policy.Config.DeleteMode == WriteMode.On;
            if (!immediate)
            {
                var draft = await _drafts.SaveDeleteAsync(target.Title, delete, token).ConfigureAwait(false);
                return ToolOutcome.Ok(new
                {
                    status = "draft",
                    draft_id = draft.Id,
                    chat = target.Id,
                    count = ids.Count,
                    for_everyone = forEveryone,
                    message = $"deletion saved as draft {draft.Id}; the owner deletes with `tgmcp approve {draft.Id}` (nothing was deleted)",
                }, target.Id, ids.Count, gate.Decision, $"delete draft {draft.Id} saved, {ids.Count} message(s){(forEveryone ? ", for everyone" : "")}");
            }
            var done = await OwnMessageGate.DeleteAsync(_c, delete, token).ConfigureAwait(false);
            return ToolOutcome.Ok(done, target.Id, ids.Count, gate.Decision, $"deleted {ids.Count} message(s){(forEveryone ? " for everyone" : "")}");
        }, ct);
}

/// <summary>Shared checks for editing and deleting own messages.</summary>
internal static class OwnMessageGate
{
    internal sealed record Gate(ToolOutcome? Denied, Decision? Decision);

    public static Task<Gate> CheckEditAsync(ToolContext c, TgChat target, PendingEdit e, CancellationToken ct) =>
        CheckAsync(c, target, [e.MessageId], Need.EditOwn, p => p.CanBeEdited ? null : "can no longer be edited (too old or not a text message)", ct);

    public static Task<Gate> CheckDeleteAsync(ToolContext c, TgChat target, PendingDelete d, CancellationToken ct) =>
        CheckAsync(c, target, d.MessageIds, Need.DeleteOwn, p => d.ForEveryone
            ? (p.CanBeDeletedForAllUsers ? null : "can't be deleted for everyone")
            : (p.CanBeDeletedOnlyForSelf || p.CanBeDeletedForAllUsers ? null : "can't be deleted"), ct);

    private static async Task<Gate> CheckAsync(ToolContext c, TgChat target, IReadOnlyList<long> ids, Need feature,
        Func<TgMessageProperties, string?> capability, CancellationToken ct)
    {
        var d = c.Policy.Check(target.Info, Need.Write, feature);
        if (!d.Allowed) return new Gate(c.Deny(d, target), null);
        foreach (var id in ids)
        {
            if (!await c.SentLog.ContainsAsync(target.Id, id, ct).ConfigureAwait(false))
                return new Gate(ToolOutcome.Refused($"message {id} was not sent through TgMcp; only those can be edited or deleted", target.Id, d.Rule), null);
            var m = await c.Telegram.GetMessageAsync(target.Id, id, ct).ConfigureAwait(false);
            if (m is null) return new Gate(ToolOutcome.Failed($"message {id} not found", target.Id, d.Rule), null);
            if (!d.TopicOk(m.TopicId)) return new Gate(c.Deny(d.Deny("message is outside allowed topics"), target, DenialKind.Topic, m.TopicId), null);
            var props = await c.Telegram.GetMessagePropertiesAsync(target.Id, id, ct).ConfigureAwait(false);
            if (capability(props) is string why) return new Gate(ToolOutcome.Failed($"message {id} {why}", target.Id, d.Rule), null);
        }
        return new Gate(null, d);
    }

    public static async Task<object> DeleteAsync(ToolContext c, PendingDelete d, CancellationToken ct)
    {
        await c.Telegram.DeleteMessagesAsync(d.ChatId, d.MessageIds, d.ForEveryone, ct).ConfigureAwait(false);
        await c.SentLog.RemoveAsync(d.ChatId, [.. d.MessageIds], ct).ConfigureAwait(false);
        return new { status = "deleted", chat = d.ChatId, message_ids = d.MessageIds, for_everyone = d.ForEveryone };
    }
}
