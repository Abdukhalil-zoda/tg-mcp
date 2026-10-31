namespace TgMcp.Application.Telegram;

/// <summary>
/// Messages this server sent (send_message, approved drafts, forwards). Only these may be edited or deleted
/// by edit_message / delete_message. Stores chat and message ids only, never text.
/// </summary>
public interface ISentLog
{
    /// <summary>Remembers a message this server sent.</summary>
    Task RecordAsync(long chatId, long messageId, CancellationToken ct);

    /// <summary>True if this server sent the message.</summary>
    Task<bool> ContainsAsync(long chatId, long messageId, CancellationToken ct);

    /// <summary>Forgets deleted messages.</summary>
    Task RemoveAsync(long chatId, IReadOnlyCollection<long> messageIds, CancellationToken ct);
}
