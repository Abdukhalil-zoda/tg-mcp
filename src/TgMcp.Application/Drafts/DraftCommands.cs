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

    /// <summary>Deletes a draft without sending it.</summary>
    public async Task<bool> RejectAsync(string id, CancellationToken ct)
    {
        var draft = await _drafts.GetAsync(id, ct).ConfigureAwait(false);
        if (draft is null) return false;
        await _drafts.DeleteAsync(id, ct).ConfigureAwait(false);
        _audit.Write(new AuditEntry(_c.Time.GetUtcNow(), "reject", draft.Message.ChatId, 0, true, "owner", $"draft {id} rejected"));
        return true;
    }
}
