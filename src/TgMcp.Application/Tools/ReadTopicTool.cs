using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>read_topic: history of one forum topic. Needs read, and the topic must be in Topics if Topics is set.</summary>
public sealed class ReadTopicTool
{
    private readonly ToolContext _c;

    /// <summary>Creates the tool.</summary>
    public ReadTopicTool(ToolContext context) => _c = context;

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string chat, int topicId, int limit, long fromMessageId, CancellationToken ct) =>
        _c.Runner.RunAsync("read_topic", [Need.Read], async token =>
        {
            if (topicId < 1) throw new ArgumentException("topic_id must be positive", nameof(topicId));
            var target = await _c.Chats.ResolveAsync(chat, token).ConfigureAwait(false);
            var d = _c.Policy.Check(target.Info, Need.Read);
            if (!d.Allowed) return _c.Deny(d, target);
            if (!d.TopicOk(topicId)) return _c.Deny(d.Deny($"topic {topicId} not in Topics"), target, DenialKind.Topic, topicId);
            // Topic history may not tag every message with a topic id; the requested topic is authoritative here.
            var scoped = d with { Topics = null };
            var (kept, _) = await MessageCollector.CollectAsync(
                (from, n) => _c.Telegram.GetTopicHistoryAsync(target.Id, topicId, from, n, token),
                fromMessageId, _c.ClampLimit(limit), scoped, _c.Time.GetUtcNow()).ConfigureAwait(false);
            return ToolOutcome.Ok(ToolContext.MessagesPayload(target, kept), target.Id, kept.Count, d);
        }, ct);
}
