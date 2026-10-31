using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>
/// react: adds an emoji reaction as the owner, or removes the owner's reactions (emoji null). Needs read,
/// the react feature and a write mode: "on" reacts at once, "draft" saves a reaction draft for <c>tgmcp approve</c>,
/// "off" is denied. Only emoji reactions offered for the message (getMessageAvailableReactions) and usable
/// without Premium are accepted; paid and custom-emoji reactions are not supported.
/// </summary>
public sealed class ReactTool
{
    private readonly ToolContext _c;
    private readonly IDraftStore _drafts;
    private readonly ISendLimiter _limiter;

    /// <summary>Creates the tool.</summary>
    public ReactTool(ToolContext context, IDraftStore drafts, ISendLimiter limiter)
    {
        _c = context;
        _drafts = drafts;
        _limiter = limiter;
    }

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string chat, long messageId, string? emoji, CancellationToken ct) =>
        _c.Runner.RunAsync("react", [Need.Read, Need.React, Need.Write], async token =>
        {
            var wanted = string.IsNullOrWhiteSpace(emoji) ? null : emoji.Trim();
            if (messageId <= 0) throw new ArgumentException("message_id must be positive", nameof(messageId));
            var target = await _c.Chats.ResolveAsync(chat, token).ConfigureAwait(false);
            var gate = await ReactionGate.CheckAsync(_c, target, messageId, wanted, token).ConfigureAwait(false);
            if (gate.Denied is not null) return gate.Denied;
            var d = gate.Decision!;
            if (!_limiter.TryAcquire(target.Id))
                return ToolOutcome.Refused($"send limit for this chat reached (limits.sendsPerMinute={_c.Policy.Config.SendsPerMinute})", target.Id, "limits");

            if (d.Write == WriteMode.Draft)
            {
                var draft = await _drafts.SaveReactionAsync(target.Title, new PendingReaction(target.Id, messageId, wanted), token).ConfigureAwait(false);
                var payload = new
                {
                    status = "draft",
                    draft_id = draft.Id,
                    chat = target.Id,
                    message_id = messageId,
                    emoji = wanted,
                    message = $"reaction saved as draft {draft.Id}; the owner applies it with `tgmcp approve {draft.Id}` (nothing was changed)",
                };
                return ToolOutcome.Ok(payload, target.Id, 1, d, $"reaction draft {draft.Id} saved on message {messageId}");
            }
            var done = await ReactionGate.ApplyAsync(_c.Telegram, target.Id, messageId, wanted, gate.Options!, token).ConfigureAwait(false);
            return ToolOutcome.Ok(done.Payload, target.Id, 1, d, done.Audit);
        }, ct);
}

/// <summary>Shared checks and actions for react and its drafts.</summary>
internal static class ReactionGate
{
    internal sealed record Gate(ToolOutcome? Denied, Decision? Decision, TgReactionOptions? Options);

    internal sealed record Applied(object Payload, string Audit);

    /// <summary>Policy (read + react + write), message visibility (sinceDays/topics) and reaction availability.</summary>
    public static async Task<Gate> CheckAsync(ToolContext c, TgChat target, long messageId, string? emoji, CancellationToken ct)
    {
        var d = c.Policy.Check(target.Info, Need.Read, Need.React, Need.Write);
        if (!d.Allowed) return new Gate(c.Deny(d, target), null, null);
        var m = await c.Telegram.GetMessageAsync(target.Id, messageId, ct).ConfigureAwait(false);
        if (m is null) return new Gate(ToolOutcome.Failed("message not found", target.Id, d.Rule), null, null);
        if (!d.DateOk(m.Date, c.Time.GetUtcNow())) return new Gate(c.Deny(d.Deny("older than SinceDays"), target, DenialKind.TooOld), null, null);
        if (!d.TopicOk(m.TopicId)) return new Gate(c.Deny(d.Deny("message is outside allowed topics"), target, DenialKind.Topic, m.TopicId), null, null);

        var options = await c.Telegram.GetReactionOptionsAsync(target.Id, messageId, ct).ConfigureAwait(false);
        if (emoji is null)
        {
            return options.Chosen.Count == 0
                ? new Gate(ToolOutcome.Failed("the owner has no reaction on this message to remove", target.Id, d.Rule), null, null)
                : new Gate(null, d, options);
        }
        if (options.Unavailable is not null && options.Emojis.Count == 0)
            return new Gate(ToolOutcome.Failed($"reactions are not available on this message ({options.Unavailable})", target.Id, d.Rule), null, null);
        if (options.PremiumOnly.Contains(emoji))
            return new Gate(ToolOutcome.Failed($"the reaction {emoji} needs Telegram Premium", target.Id, d.Rule), null, null);
        if (!options.Emojis.Contains(emoji))
        {
            var list = string.Join(" ", options.Emojis.Take(30));
            return new Gate(ToolOutcome.Failed($"{emoji} is not an available reaction for this message; available: {list}", target.Id, d.Rule), null, null);
        }
        return new Gate(null, d, options);
    }

    /// <summary>Adds <paramref name="emoji"/>, or removes every reaction the owner chose when it is null.</summary>
    public static async Task<Applied> ApplyAsync(ITelegramClient telegram, long chatId, long messageId, string? emoji, TgReactionOptions options, CancellationToken ct)
    {
        if (emoji is null)
        {
            foreach (var e in options.Chosen) await telegram.RemoveReactionAsync(chatId, messageId, e, ct).ConfigureAwait(false);
            return new Applied(new { status = "removed", chat = chatId, message_id = messageId, removed = options.Chosen }, $"reactions removed from message {messageId}");
        }
        await telegram.AddReactionAsync(chatId, messageId, emoji, ct).ConfigureAwait(false);
        return new Applied(new { status = "added", chat = chatId, message_id = messageId, emoji }, $"reaction {emoji} added on message {messageId}");
    }
}
