using TgMcp.Application.Audit;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;

namespace TgMcp.Application.Drafts;

/// <summary>Owner commands for drafts: list, approve (send), reject (delete).</summary>
public sealed class DraftCommands
{
    private readonly IDraftStore _drafts;
    private readonly ToolContext _c;
    private readonly IAuditSink _audit;

    /// <summary>Creates the commands.</summary>
    public DraftCommands(IDraftStore drafts, ToolContext context, IAuditSink audit)
    {
        _drafts = drafts;
        _c = context;
        _audit = audit;
    }

    /// <summary>All drafts, oldest first.</summary>
    public Task<IReadOnlyList<Draft>> ListAsync(CancellationToken ct) => _drafts.ListAsync(ct);

    /// <summary>
    /// Sends a draft as the owner, then deletes it. The policy is checked again (deny rules and
    /// <c>"write": "off"</c> still block it); the call is audited like a tool call.
    /// </summary>
    public async Task<ToolResult> ApproveAsync(string id, CancellationToken ct)
    {
        var draft = await _drafts.GetAsync(id, ct).ConfigureAwait(false);
        if (draft is null) return ToolResult.Error($"no draft with id {id}");
        if (draft.Kind == Draft.JoinKind && draft.Join is { } join) return await ApproveJoinAsync(draft, join, ct).ConfigureAwait(false);
        if (draft.Kind == Draft.EditKind && draft.Edit is { } edit) return await ApproveEditAsync(draft, edit, ct).ConfigureAwait(false);
        if (draft.Kind == Draft.DeleteKind && draft.Delete is { } delete) return await ApproveDeleteAsync(draft, delete, ct).ConfigureAwait(false);
        if (draft.Kind == Draft.ForwardKind && draft.Forward is { } forward) return await ApproveForwardAsync(draft, forward, ct).ConfigureAwait(false);
        if (draft.Kind == Draft.ReactionKind && draft.Reaction is { } reaction) return await ApproveReactionAsync(draft, reaction, ct).ConfigureAwait(false);
        if (draft.Message is null) return ToolResult.Error($"draft {id} is malformed");
        return await _c.Runner.RunAsync("approve", [Need.Write], async token =>
        {
            var target = await _c.Telegram.GetChatAsync(draft.Message.ChatId, token).ConfigureAwait(false);
            var d = _c.Policy.Check(target.Info, Need.Write);
            if (!d.Allowed) return _c.Deny(d, target);
            var sent = await _c.Telegram.SendMessageAsync(draft.Message, token).ConfigureAwait(false);
            await _c.SentLog.RecordAsync(sent.ChatId, sent.MessageId, token).ConfigureAwait(false);
            await _drafts.DeleteAsync(draft.Id, token).ConfigureAwait(false);
            return ToolOutcome.Ok(SendMessageTool.Sent(sent), target.Id, 1, d, $"draft {draft.Id} approved and sent, length {draft.Message.Text.Length}");
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs a drafted join. Re-checks features.join, join.mode (not "off") and deny rules on the resolved chat;
    /// join.allow is not required because the owner approves this specific target.
    /// </summary>
    private Task<ToolResult> ApproveJoinAsync(Draft draft, PendingJoin join, CancellationToken ct) =>
        _c.Runner.RunAsync("approve", [Need.Join], async token =>
        {
            var target = join.Target;
            var jd = _c.Policy.CheckJoin(target);
            if (jd.Global is not null || jd.Mode == WriteMode.Off)
            {
                return new ToolOutcome(ToolResult.Error(JoinSupport.Denied(jd, _c.Policy.Config)),
                    join.ChatId, 0, false, "join", JoinSupport.AuditReason(target, jd.Mode, "approve denied: " + jd.Reason));
            }
            var preview = await _c.Telegram.PreviewJoinAsync(target, token).ConfigureAwait(false);
            if (JoinSupport.HardDenied(_c.Policy, preview))
            {
                return new ToolOutcome(ToolResult.Error(Denials.HardDeny), preview.ChatId, 0, false, "deny",
                    JoinSupport.AuditReason(target, jd.Mode, "approve denied by rule"));
            }
            var joined = preview.AlreadyMember
                ? new TgJoinResult("already_member", preview.ChatId, preview.Title)
                : await _c.Telegram.JoinAsync(target, token).ConfigureAwait(false);
            await _drafts.DeleteAsync(draft.Id, token).ConfigureAwait(false);
            return new ToolOutcome(ToolResult.Ok(JoinChatTool.Result(joined, preview)), joined.ChatId, 1, true, "owner",
                JoinSupport.AuditReason(target, jd.Mode, $"draft {draft.Id} approved, {joined.Status}"));
        }, ct);

    /// <summary>Applies a drafted edit after re-checking the policy, the sent-log and editability.</summary>
    private Task<ToolResult> ApproveEditAsync(Draft draft, PendingEdit edit, CancellationToken ct) =>
        _c.Runner.RunAsync("approve", [Need.Write, Need.EditOwn], async token =>
        {
            var target = await _c.Telegram.GetChatAsync(edit.ChatId, token).ConfigureAwait(false);
            var gate = await OwnMessageGate.CheckEditAsync(_c, target, edit, token).ConfigureAwait(false);
            if (gate.Denied is not null) return gate.Denied;
            await _c.Telegram.EditMessageTextAsync(edit.ChatId, edit.MessageId, edit.Text, token).ConfigureAwait(false);
            await _drafts.DeleteAsync(draft.Id, token).ConfigureAwait(false);
            return ToolOutcome.Ok(new { status = "edited", chat = edit.ChatId, message_id = edit.MessageId }, target.Id, 1, gate.Decision!,
                $"draft {draft.Id} approved, edited message {edit.MessageId}, length {edit.Text.Length}");
        }, ct);

    /// <summary>Performs a drafted deletion after re-checking the policy, the sent-log and deletability.</summary>
    private Task<ToolResult> ApproveDeleteAsync(Draft draft, PendingDelete delete, CancellationToken ct) =>
        _c.Runner.RunAsync("approve", [Need.Write, Need.DeleteOwn], async token =>
        {
            var target = await _c.Telegram.GetChatAsync(delete.ChatId, token).ConfigureAwait(false);
            var gate = await OwnMessageGate.CheckDeleteAsync(_c, target, delete, token).ConfigureAwait(false);
            if (gate.Denied is not null) return gate.Denied;
            var done = await OwnMessageGate.DeleteAsync(_c, delete, token).ConfigureAwait(false);
            await _drafts.DeleteAsync(draft.Id, token).ConfigureAwait(false);
            return ToolOutcome.Ok(done, target.Id, delete.MessageIds.Count, gate.Decision!,
                $"draft {draft.Id} approved, deleted {delete.MessageIds.Count} message(s){(delete.ForEveryone ? " for everyone" : "")}");
        }, ct);

    /// <summary>Performs a drafted forward after re-checking both chats and every message.</summary>
    private Task<ToolResult> ApproveForwardAsync(Draft draft, PendingForward forward, CancellationToken ct) =>
        _c.Runner.RunAsync("approve", [Need.Read, Need.Forward, Need.Write], async token =>
        {
            var source = await _c.Telegram.GetChatAsync(forward.FromChatId, token).ConfigureAwait(false);
            var target = await _c.Telegram.GetChatAsync(forward.ToChatId, token).ConfigureAwait(false);
            var gate = await ForwardGate.CheckAsync(_c, source, target, forward, token).ConfigureAwait(false);
            if (gate.Denied is not null) return gate.Denied;
            var done = await ForwardGate.ApplyAsync(_c.Telegram, _c.SentLog, forward, token).ConfigureAwait(false);
            await _drafts.DeleteAsync(draft.Id, token).ConfigureAwait(false);
            return ToolOutcome.Ok(done, target.Id, forward.MessageIds.Count, gate.Target!,
                $"draft {draft.Id} approved, forwarded {forward.MessageIds.Count} message(s) from {source.Id}");
        }, ct);

    /// <summary>Applies a drafted reaction after re-checking the policy and the available reactions.</summary>
    private Task<ToolResult> ApproveReactionAsync(Draft draft, PendingReaction reaction, CancellationToken ct) =>
        _c.Runner.RunAsync("approve", [Need.Read, Need.React, Need.Write], async token =>
        {
            var target = await _c.Telegram.GetChatAsync(reaction.ChatId, token).ConfigureAwait(false);
            var gate = await ReactionGate.CheckAsync(_c, target, reaction.MessageId, reaction.Emoji, token).ConfigureAwait(false);
            if (gate.Denied is not null) return gate.Denied;
            var done = await ReactionGate.ApplyAsync(_c.Telegram, target.Id, reaction.MessageId, reaction.Emoji, gate.Options!, token).ConfigureAwait(false);
            await _drafts.DeleteAsync(draft.Id, token).ConfigureAwait(false);
            return ToolOutcome.Ok(done.Payload, target.Id, 1, gate.Decision!, $"draft {draft.Id} approved, {done.Audit}");
        }, ct);

    /// <summary>Deletes a draft without sending it.</summary>
    public async Task<bool> RejectAsync(string id, CancellationToken ct)
    {
        var draft = await _drafts.GetAsync(id, ct).ConfigureAwait(false);
        if (draft is null) return false;
        await _drafts.DeleteAsync(id, ct).ConfigureAwait(false);
        _audit.Write(new AuditEntry(_c.Time.GetUtcNow(), "reject", draft.Message?.ChatId ?? draft.Join?.ChatId ?? draft.Reaction?.ChatId ?? draft.Forward?.ToChatId ?? draft.Edit?.ChatId ?? draft.Delete?.ChatId, 0, true, "owner", $"{draft.Kind} draft {id} rejected"));
        return true;
    }
}
