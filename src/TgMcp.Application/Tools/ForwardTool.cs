using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>
/// forward_message: forwards messages from a readable chat to a writable one, as the owner. Source: read +
/// "forward" (features.forward), each message within sinceDays/topics and forwardable. Destination: a write mode
/// (features.send): "on" forwards at once, "draft" saves a forward draft for <c>tgmcp approve</c>, "off" is denied.
/// The destination does not need read. Deny rules on either side win.
/// </summary>
public sealed class ForwardTool
{
    /// <summary>Most messages per call.</summary>
    public const int MaxMessages = 20;

    private readonly ToolContext _c;
    private readonly IDraftStore _drafts;
    private readonly ISendLimiter _limiter;

    /// <summary>Creates the tool.</summary>
    public ForwardTool(ToolContext context, IDraftStore drafts, ISendLimiter limiter)
    {
        _c = context;
        _drafts = drafts;
        _limiter = limiter;
    }

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string fromChat, IReadOnlyList<long> messageIds, string toChat, int toTopicId, bool sendCopy, CancellationToken ct) =>
        _c.Runner.RunAsync("forward_message", [Need.Read, Need.Forward, Need.Write], async token =>
        {
            var ids = (messageIds ?? []).Distinct().ToList();
            if (ids.Count == 0 || ids.Any(i => i <= 0)) throw new ArgumentException("message_ids must be positive message ids", nameof(messageIds));
            if (ids.Count > MaxMessages) throw new ArgumentException($"at most {MaxMessages} messages per call", nameof(messageIds));
            if (toTopicId < 0) throw new ArgumentException("to_topic_id must not be negative", nameof(toTopicId));
            var source = await _c.Chats.ResolveAsync(fromChat, token).ConfigureAwait(false);
            var target = await _c.Chats.ResolveAsync(toChat, token).ConfigureAwait(false);
            var forward = new PendingForward(source.Id, ids, target.Id, toTopicId > 0 ? toTopicId : null, sendCopy);
            var gate = await ForwardGate.CheckAsync(_c, source, target, forward, token).ConfigureAwait(false);
            if (gate.Denied is not null) return gate.Denied;
            if (!_limiter.TryAcquire(target.Id))
                return ToolOutcome.Refused($"send limit for this chat reached (limits.sendsPerMinute={_c.Policy.Config.SendsPerMinute})", target.Id, "limits");

            if (gate.Target!.Write == WriteMode.Draft)
            {
                var draft = await _drafts.SaveForwardAsync(target.Title, forward, token).ConfigureAwait(false);
                var payload = new
                {
                    status = "draft",
                    draft_id = draft.Id,
                    from_chat = source.Id,
                    to_chat = target.Id,
                    count = ids.Count,
                    message = $"forward saved as draft {draft.Id}; the owner forwards it with `tgmcp approve {draft.Id}` (nothing was forwarded)",
                };
                return ToolOutcome.Ok(payload, target.Id, ids.Count, gate.Target, $"forward draft {draft.Id} saved, {ids.Count} message(s) from {source.Id}");
            }
            var done = await ForwardGate.ApplyAsync(_c.Telegram, forward, token).ConfigureAwait(false);
            return ToolOutcome.Ok(done, target.Id, ids.Count, gate.Target, $"forwarded {ids.Count} message(s) from {source.Id}");
        }, ct);
}

/// <summary>Shared checks and action for forward_message and its drafts.</summary>
internal static class ForwardGate
{
    internal sealed record Gate(ToolOutcome? Denied, Decision? Source, Decision? Target);

    /// <summary>Policy on both chats, message visibility and forwardability, destination topics.</summary>
    public static async Task<Gate> CheckAsync(ToolContext c, TgChat source, TgChat target, PendingForward f, CancellationToken ct)
    {
        var ds = c.Policy.Check(source.Info, Need.Read, Need.Forward);
        if (!ds.Allowed) return new Gate(c.Deny(ds, source), null, null);
        var dt = c.Policy.Check(target.Info, Need.Write);
        if (!dt.Allowed) return new Gate(c.Deny(dt, target), null, null);
        if (dt.Topics is not null)
        {
            if (f.ToTopicId is not int topic)
            {
                return new Gate(ToolOutcome.Denied(dt.Deny("to_topic_id required"), target.Id,
                    $"{Denials.Describe(target)} may only be written to in topics [{string.Join(", ", dt.Topics.Order())}]; pass to_topic_id."), null, null);
            }
            if (!dt.TopicOk(topic)) return new Gate(c.Deny(dt.Deny($"topic {topic} not in topics"), target, DenialKind.Topic, topic), null, null);
        }
        var now = c.Time.GetUtcNow();
        foreach (var id in f.MessageIds)
        {
            var m = await c.Telegram.GetMessageAsync(source.Id, id, ct).ConfigureAwait(false);
            if (m is null) return new Gate(ToolOutcome.Failed($"message {id} not found", source.Id, ds.Rule), null, null);
            if (!ds.DateOk(m.Date, now)) return new Gate(c.Deny(ds.Deny("older than SinceDays"), source, DenialKind.TooOld), null, null);
            if (!ds.TopicOk(m.TopicId)) return new Gate(c.Deny(ds.Deny("message is outside allowed topics"), source, DenialKind.Topic, m.TopicId), null, null);
            var props = await c.Telegram.GetMessagePropertiesAsync(source.Id, id, ct).ConfigureAwait(false);
            if (!props.CanBeForwarded)
                return new Gate(ToolOutcome.Failed($"message {id} can't be forwarded (protected content or a service message)", source.Id, ds.Rule), null, null);
        }
        return new Gate(null, ds, dt);
    }

    /// <summary>Forwards the messages.</summary>
    public static async Task<object> ApplyAsync(ITelegramClient telegram, PendingForward f, CancellationToken ct)
    {
        var ids = await telegram.ForwardMessagesAsync(f.FromChatId, f.MessageIds, f.ToChatId, f.ToTopicId, f.SendCopy, ct).ConfigureAwait(false);
        return new { status = "forwarded", from_chat = f.FromChatId, to_chat = f.ToChatId, message_ids = ids };
    }
}
