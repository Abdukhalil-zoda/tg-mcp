using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>
/// send_message: sends as the owner (<c>"write": "on"</c>) or saves a draft for the owner to approve
/// (<c>"write": "draft"</c>). Needs <c>features.send</c>. The text is never audited, only its length.
/// </summary>
public sealed class SendMessageTool
{
    private readonly ToolContext _c;
    private readonly IDraftStore _drafts;
    private readonly ISendLimiter _sends;
    private readonly INewChatLimiter _newChats;

    /// <summary>Creates the tool.</summary>
    public SendMessageTool(ToolContext context, IDraftStore drafts, ISendLimiter sends, INewChatLimiter newChats)
    {
        _c = context;
        _drafts = drafts;
        _sends = sends;
        _newChats = newChats;
    }

    /// <summary>Runs the tool for a chat (id or @username).</summary>
    public Task<ToolResult> RunAsync(string chat, string text, long replyToMessageId, int topicId, bool disableNotification, CancellationToken ct) =>
        RunAsync(chat, text, replyToMessageId, topicId, disableNotification, null, null, ct);

    /// <summary>Runs the tool; with <paramref name="phone"/> instead of a chat, the message goes to a person by phone number.</summary>
    public Task<ToolResult> RunAsync(string? chat, string text, long replyToMessageId, int topicId, bool disableNotification,
        string? phone, string? contactName, CancellationToken ct) =>
        _c.Runner.RunAsync("send_message", [Need.Write], async token =>
        {
            ValidateText(text);
            if (!string.IsNullOrWhiteSpace(phone))
            {
                if (!string.IsNullOrWhiteSpace(chat)) throw new ArgumentException("pass either chat or phone, not both", nameof(phone));
                if (replyToMessageId != 0 || topicId != 0) throw new ArgumentException("reply_to_message_id and topic_id can't be used with phone", nameof(phone));
                return await PhoneMessages.SendAsync(_c, _drafts, _newChats, phone, contactName, text, disableNotification, token).ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(chat)) throw new ArgumentException("chat is required (or phone, if the owner allows it)", nameof(chat));
            if (replyToMessageId < 0) throw new ArgumentException("reply_to_message_id must not be negative", nameof(replyToMessageId));
            if (topicId < 0) throw new ArgumentException("topic_id must not be negative", nameof(topicId));
            var target = await _c.Chats.ResolveAsync(chat, token).ConfigureAwait(false);
            var d = _c.Policy.Check(target.Info, Need.Write);
            if (!d.Allowed) return _c.Deny(d, target);
            var topic = topicId > 0 ? topicId : (int?)null;
            var gate = await CheckTopics(d, target, topic, replyToMessageId, token).ConfigureAwait(false);
            if (gate is not null) return gate;
            if (!_sends.TryAcquire(target.Id))
            {
                return ToolOutcome.Refused($"send limit for this chat reached (limits.sendsPerMinute={_c.Policy.Config.SendsPerMinute})", target.Id, "limits");
            }

            var message = new OutgoingMessage(target.Id, text, replyToMessageId, topic, disableNotification);
            if (d.Write == WriteMode.Draft)
            {
                var draft = await _drafts.SaveAsync(target.Title, message, token).ConfigureAwait(false);
                var payload = new
                {
                    status = "draft",
                    draft_id = draft.Id,
                    chat = target.Id,
                    message = $"draft {draft.Id} saved; the owner sends it with `tgmcp approve {draft.Id}` (nothing was sent)",
                };
                return ToolOutcome.Ok(payload, target.Id, 1, d, $"draft {draft.Id} saved, length {text.Length}");
            }

            var sent = await _c.Telegram.SendMessageAsync(message, token).ConfigureAwait(false);
            await _c.SentLog.RecordAsync(sent.ChatId, sent.MessageId, token).ConfigureAwait(false);
            return ToolOutcome.Ok(Sent(sent), target.Id, 1, d, $"sent, length {text.Length}");
        }, ct);

    /// <summary>Payload for a confirmed message.</summary>
    internal static object Sent(TgSent sent) => new
    {
        status = "sent",
        chat = sent.ChatId,
        message_id = sent.MessageId,
        date_utc = sent.Date.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Plain text, 1..4096 characters.</summary>
    internal static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is empty", nameof(text));
        if (text.Length > OutgoingMessage.MaxLength)
            throw new ArgumentException($"text is {text.Length} characters; the limit is {OutgoingMessage.MaxLength}", nameof(text));
    }

    /// <summary>If the rule restricts topics, the message must go to (and reply within) an allowed topic.</summary>
    private async Task<ToolOutcome?> CheckTopics(Decision d, TgChat target, int? topic, long replyTo, CancellationToken ct)
    {
        if (d.Topics is null) return null;
        if (topic is null)
        {
            var allowed = string.Join(", ", d.Topics.Order());
            return ToolOutcome.Denied(d.Deny("topic_id required"), target.Id,
                $"{Denials.Describe(target)} may only be written to in topics [{allowed}]; pass topic_id.");
        }
        if (!d.TopicOk(topic)) return _c.Deny(d.Deny($"topic {topic} not in topics"), target, DenialKind.Topic, topic);
        if (replyTo > 0)
        {
            var original = await _c.Telegram.GetMessageAsync(target.Id, replyTo, ct).ConfigureAwait(false);
            if (original is null) return ToolOutcome.Failed("reply_to_message_id not found", target.Id, d.Rule);
            if (!d.TopicOk(original.TopicId))
                return _c.Deny(d.Deny("reply target outside allowed topics"), target, DenialKind.Topic, original.TopicId);
        }
        return null;
    }
}
