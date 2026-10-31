using static TdLib.TdApi;

namespace TgMcp.Infrastructure.Telegram;

/// <summary>
/// Decides which TDLib updates feed inbound events. A message still being sent (pending) carries a temporary
/// id, so it is skipped on updateNewMessage and emitted on updateMessageSendSucceeded with its final id.
/// Failed sends never become events.
/// </summary>
public static class InboundRouter
{
    /// <summary>The message to hand to the events pipeline for <paramref name="update"/>, or null.</summary>
    public static Message? ForEvents(Update update) => update switch
    {
        Update.UpdateNewMessage { Message: { SendingState: null } m } => m,
        Update.UpdateMessageSendSucceeded { Message: { } m } => m,
        _ => null,
    };
}
