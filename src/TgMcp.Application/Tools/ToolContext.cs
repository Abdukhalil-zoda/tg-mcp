using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>Dependencies shared by all read tools.</summary>
/// <param name="Runner">Call pipeline.</param>
/// <param name="Policy">Access policy.</param>
/// <param name="Chats">Chat argument resolver.</param>
/// <param name="Telegram">Telegram port.</param>
/// <param name="Time">Clock.</param>
public sealed record ToolContext(ToolRunner Runner, IPolicy Policy, ChatResolver Chats, ITelegramClient Telegram, TimeProvider Time)
{
    /// <summary>Clamps a requested count to [1, Limits__MaxMessagesPerCall].</summary>
    public int ClampLimit(int requested) => Math.Clamp(requested, 1, Policy.Config.MaxMessagesPerCall);

    /// <summary>A denial outcome with an actionable (or, for Deny rules, opaque) message.</summary>
    public ToolOutcome Deny(Decision d, TgChat? chat, DenialKind kind = DenialKind.Permission, int? topicId = null)
    {
        ArgumentNullException.ThrowIfNull(d);
        return ToolOutcome.Denied(d, chat?.Id, Denials.Message(Policy.Config, d, chat, kind, topicId));
    }

    /// <summary>Standard payload for a list of messages.</summary>
    public static object MessagesPayload(TgChat? chat, IEnumerable<TgMessage> messages) => new
    {
        note = ToolResult.ThirdPartyNote,
        chat = chat is null ? null : new { id = chat.Id, title = chat.Title, type = Names.Snake(chat.Kind) },
        messages = messages.Select(MessageView.From).ToList(),
    };
}
