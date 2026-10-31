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
        if (draft.Message is null) return ToolResult.Error($"draft {id} is malformed");
        return await _c.Runner.RunAsync("approve", [Need.Write], async token =>
        {
            var target = await _c.Telegram.GetChatAsync(draft.Message.ChatId, token).ConfigureAwait(false);
            var d = _c.Policy.Check(target.Info, Need.Write);
            if (!d.Allowed) return _c.Deny(d, target);
            var sent = await _c.Telegram.SendMessageAsync(draft.Message, token).ConfigureAwait(false);
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

    /// <summary>Deletes a draft without sending it.</summary>
    public async Task<bool> RejectAsync(string id, CancellationToken ct)
    {
        var draft = await _drafts.GetAsync(id, ct).ConfigureAwait(false);
        if (draft is null) return false;
        await _drafts.DeleteAsync(id, ct).ConfigureAwait(false);
        _audit.Write(new AuditEntry(_c.Time.GetUtcNow(), "reject", draft.Message?.ChatId ?? draft.Join?.ChatId, 0, true, "owner", $"{draft.Kind} draft {id} rejected"));
        return true;
    }
}
