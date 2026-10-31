using TgMcp.Domain;

namespace TgMcp.Application.Telegram;

/// <summary>A chat with the data the policy needs plus display data.</summary>
/// <param name="Id">TDLib chat id (supergroups and channels start with -100).</param>
/// <param name="Title">Chat title (for private chats: the user's name).</param>
/// <param name="Usernames">Active public usernames, without '@'.</param>
/// <param name="Kind">Policy chat kind.</param>
/// <param name="IsForum">True for forum supergroups (chats with topics).</param>
public sealed record TgChat(long Id, string Title, IReadOnlyList<string> Usernames, ChatKind Kind, bool IsForum = false)
{
    /// <summary>The policy view of this chat.</summary>
    public ChatInfo Info => new(Id, Title, Usernames, Kind);
}

/// <summary>A downloadable file attached to a message.</summary>
/// <param name="FileId">TDLib file id.</param>
/// <param name="Size">Size in bytes (0 if unknown).</param>
/// <param name="Kind">Media kind, e.g. photo, video, document.</param>
/// <param name="FileName">Original file name, if any.</param>
/// <param name="MimeType">MIME type, if known.</param>
public sealed record TgMedia(int FileId, long Size, string Kind, string? FileName, string? MimeType);

/// <summary>A message as returned by the Telegram port.</summary>
/// <param name="ChatId">Chat id.</param>
/// <param name="Id">Message id.</param>
/// <param name="Date">Send time (UTC).</param>
/// <param name="Sender">Display name of the sender.</param>
/// <param name="FromMe">True if sent by the account owner.</param>
/// <param name="Text">Text or caption; a placeholder like "[sticker]" for other content.</param>
/// <param name="Media">Attached media, if any.</param>
/// <param name="TopicId">Forum topic id, if the message belongs to a forum topic.</param>
/// <param name="SenderId">User or chat id of the sender, if known.</param>
/// <param name="SenderUsername">Sender's first public username, if any.</param>
/// <param name="SenderIsBot">True if the sender is a bot account.</param>
/// <param name="ReplyToMessageId">Id of the message this one replies to (same chat), if any.</param>
public sealed record TgMessage(
    long ChatId,
    long Id,
    DateTimeOffset Date,
    string Sender,
    bool FromMe,
    string Text,
    TgMedia? Media,
    int? TopicId,
    long? SenderId = null,
    string? SenderUsername = null,
    bool SenderIsBot = false,
    long? ReplyToMessageId = null)
{
    /// <summary>True if the message has an attached media file.</summary>
    public bool HasMedia => Media is not null;
}

/// <summary>State of a file in the local TDLib files directory.</summary>
/// <param name="Id">TDLib file id.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="LocalPath">Local path once downloaded.</param>
/// <param name="Completed">True when the download is complete.</param>
public sealed record TgFile(int Id, long Size, string? LocalPath, bool Completed);

/// <summary>One page of global search results.</summary>
/// <param name="Messages">Messages on this page.</param>
/// <param name="NextOffset">Offset of the next page; empty when there are no more results.</param>
public sealed record TgPage(IReadOnlyList<TgMessage> Messages, string NextOffset);

/// <summary>A plain-text message to send.</summary>
/// <param name="ChatId">Target chat.</param>
/// <param name="Text">Plain text (no markup parsing), at most <see cref="MaxLength"/> characters.</param>
/// <param name="ReplyToMessageId">Message to reply to (0 = none).</param>
/// <param name="TopicId">Forum topic to post into (null = none).</param>
/// <param name="DisableNotification">Send silently.</param>
public sealed record OutgoingMessage(long ChatId, string Text, long ReplyToMessageId, int? TopicId, bool DisableNotification)
{
    /// <summary>Telegram's limit for one text message.</summary>
    public const int MaxLength = 4096;
}

/// <summary>A message Telegram confirmed as sent.</summary>
/// <param name="ChatId">Chat id.</param>
/// <param name="MessageId">Final message id.</param>
/// <param name="Date">Server date (UTC).</param>
public sealed record TgSent(long ChatId, long MessageId, DateTimeOffset Date);

/// <summary>What a join target would join, without joining (preview_chat).</summary>
/// <param name="ChatId">Chat id if TDLib already knows the chat (public chats, or chats the owner can access).</param>
/// <param name="Title">Chat title.</param>
/// <param name="Type">"group", "supergroup", "channel", or "private"/"bot" for non-joinable targets.</param>
/// <param name="Kind">Policy chat kind (for deny rules).</param>
/// <param name="Usernames">Public usernames, if any.</param>
/// <param name="MemberCount">Member count (0 if unknown).</param>
/// <param name="JoinRequestNeeded">True if joining only sends a join request that admins must approve.</param>
/// <param name="AlreadyMember">True if the owner is already a member.</param>
public sealed record TgChatPreview(long? ChatId, string Title, string Type, ChatKind Kind, IReadOnlyList<string> Usernames,
    int MemberCount, bool JoinRequestNeeded, bool AlreadyMember)
{
    /// <summary>True for groups, supergroups and channels.</summary>
    public bool Joinable => Type is "group" or "supergroup" or "channel";
}

/// <summary>Result of a join.</summary>
/// <param name="Status">"joined", "requested" (join request sent) or "already_member".</param>
/// <param name="ChatId">The chat id, if known.</param>
/// <param name="Title">Chat title.</param>
public sealed record TgJoinResult(string Status, long? ChatId, string Title);

/// <summary>Reactions that may be put on a message.</summary>
/// <param name="Emojis">Emoji reactions usable without Telegram Premium.</param>
/// <param name="PremiumOnly">Emoji reactions that need Telegram Premium.</param>
/// <param name="Chosen">Emoji reactions the owner has already put on the message.</param>
/// <param name="Unavailable">Why reactions are unavailable (e.g. anonymous admin), or null.</param>
public sealed record TgReactionOptions(IReadOnlyList<string> Emojis, IReadOnlyList<string> PremiumOnly, IReadOnlyList<string> Chosen, string? Unavailable);

/// <summary>What may be done with a message.</summary>
/// <param name="CanBeForwarded">forwardMessages is allowed.</param>
/// <param name="CanBeEdited">editMessageText is allowed.</param>
/// <param name="CanBeDeletedForAllUsers">deleteMessages with revoke is allowed.</param>
/// <param name="CanBeDeletedOnlyForSelf">deleteMessages without revoke is allowed.</param>
public sealed record TgMessageProperties(bool CanBeForwarded, bool CanBeEdited, bool CanBeDeletedForAllUsers, bool CanBeDeletedOnlyForSelf);

/// <summary>A private chat found by phone number.</summary>
/// <param name="Chat">The private chat.</param>
/// <param name="Imported">True if a contact had to be imported to reach the user.</param>
public sealed record TgPhoneChat(TgChat Chat, bool Imported);

/// <summary>Telegram is not usable right now (no credentials, not logged in, closed).</summary>
/// <param name="message">Reason, safe to show to the client.</param>
public sealed class TelegramUnavailableException(string message) : Exception(message);

/// <summary>A Telegram error (code and TDLib message). Never contains message content.</summary>
/// <param name="code">TDLib error code (e.g. 400, 429).</param>
/// <param name="message">TDLib error message.</param>
public sealed class TelegramCallException(int code, string message) : Exception($"telegram error {code}: {message}")
{
    /// <summary>TDLib error code.</summary>
    public int Code { get; } = code;

    /// <summary>The raw TDLib error message.</summary>
    public string TdMessage { get; } = message;
}

/// <summary>Read-only subset of Telegram used by the read tools. Implemented by the TDLib adapter.</summary>
public interface ITelegramClient
{
    /// <summary>Throws <see cref="TelegramUnavailableException"/> unless logged in and ready.</summary>
    Task EnsureReadyAsync(CancellationToken ct);

    /// <summary>Ids of the first <paramref name="limit"/> chats of the main chat list, in list order.</summary>
    Task<IReadOnlyList<long>> GetChatIdsAsync(int limit, CancellationToken ct);

    /// <summary>Chat details by id.</summary>
    Task<TgChat> GetChatAsync(long chatId, CancellationToken ct);

    /// <summary>Resolves a public username; null if not found.</summary>
    Task<TgChat?> SearchPublicChatAsync(string username, CancellationToken ct);

    /// <summary>History, newest first, starting before <paramref name="fromMessageId"/> (0 = latest).</summary>
    Task<IReadOnlyList<TgMessage>> GetChatHistoryAsync(long chatId, long fromMessageId, int limit, CancellationToken ct);

    /// <summary>Forum topic history, newest first.</summary>
    Task<IReadOnlyList<TgMessage>> GetTopicHistoryAsync(long chatId, int topicId, long fromMessageId, int limit, CancellationToken ct);

    /// <summary>One message; null if it does not exist.</summary>
    Task<TgMessage?> GetMessageAsync(long chatId, long messageId, CancellationToken ct);

    /// <summary>Text search inside one chat, newest first.</summary>
    Task<IReadOnlyList<TgMessage>> SearchChatMessagesAsync(long chatId, string query, long fromMessageId, int limit, CancellationToken ct);

    /// <summary>Text search across all chats (one page).</summary>
    Task<TgPage> SearchMessagesAsync(string query, string offset, int limit, CancellationToken ct);

    /// <summary>Downloads a whole file into the TDLib files directory and waits for completion.</summary>
    Task<TgFile> DownloadFileAsync(int fileId, CancellationToken ct);

    /// <summary>
    /// Sends a plain-text message as the account owner and waits until Telegram confirms it
    /// (temporary id replaced by the final one). Throws <see cref="TelegramCallException"/> on failure or timeout.
    /// </summary>
    Task<TgSent> SendMessageAsync(OutgoingMessage message, CancellationToken ct);

    /// <summary>
    /// Marks the chat as read up to <paramref name="upToMessageId"/> (viewMessages with force_read), or up to the
    /// last message when null, in which case mentions and reactions are cleared too. Returns the id marked, or null if the chat is empty.
    /// </summary>
    Task<long?> MarkReadAsync(long chatId, long? upToMessageId, CancellationToken ct);

    /// <summary>Available and already chosen emoji reactions for a message (getMessageAvailableReactions + the message's reactions).</summary>
    Task<TgReactionOptions> GetReactionOptionsAsync(long chatId, long messageId, CancellationToken ct);

    /// <summary>Adds an emoji reaction as the owner (addMessageReaction).</summary>
    Task AddReactionAsync(long chatId, long messageId, string emoji, CancellationToken ct);

    /// <summary>Removes the owner's emoji reaction (removeMessageReaction).</summary>
    Task RemoveReactionAsync(long chatId, long messageId, string emoji, CancellationToken ct);

    /// <summary>Resolves a join target without joining (searchPublicChat / checkChatInviteLink).</summary>
    Task<TgChatPreview> PreviewJoinAsync(JoinTarget target, CancellationToken ct);

    /// <summary>Joins (joinChat / joinChatByInviteLink). A join request counts as success with status "requested".</summary>
    Task<TgJoinResult> JoinAsync(JoinTarget target, CancellationToken ct);

    /// <summary>Message capabilities (getMessageProperties).</summary>
    Task<TgMessageProperties> GetMessagePropertiesAsync(long chatId, long messageId, CancellationToken ct);

    /// <summary>
    /// Forwards messages (forwardMessages) and waits for Telegram to confirm them. Returns the final ids in the
    /// destination chat. <paramref name="sendCopy"/> sends copies without the "forwarded from" header.
    /// </summary>
    Task<IReadOnlyList<long>> ForwardMessagesAsync(long fromChatId, IReadOnlyList<long> messageIds, long toChatId, int? toTopicId, bool sendCopy, CancellationToken ct);

    /// <summary>Replaces the text of a message (editMessageText, plain text, link previews off).</summary>
    Task EditMessageTextAsync(long chatId, long messageId, string text, CancellationToken ct);

    /// <summary>Deletes messages (deleteMessages); <paramref name="forEveryone"/> = revoke for all users.</summary>
    Task DeleteMessagesAsync(long chatId, IReadOnlyList<long> messageIds, bool forEveryone, CancellationToken ct);

    /// <summary>Telegram's AI summary of a message (summarizeMessage), optionally translated. Usually needs Telegram Premium.</summary>
    Task<string> SummarizeMessageAsync(long chatId, long messageId, string? translateTo, CancellationToken ct);

    /// <summary>Telegram's translation of a message (translateMessageText). May need Telegram Premium.</summary>
    Task<string> TranslateMessageAsync(long chatId, long messageId, string toLanguage, CancellationToken ct);

    /// <summary>
    /// The private chat with the Telegram user who has this phone number. Looks the number up locally first;
    /// otherwise imports exactly one contact (importContacts) named <paramref name="contactName"/>. Null if the number
    /// is not on Telegram or the user hides it.
    /// </summary>
    Task<TgPhoneChat?> ResolvePhoneAsync(string e164, string contactName, CancellationToken ct);

    /// <summary>Chat folder title to the ids of the chats currently in that folder.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlySet<long>>> GetFolderChatsAsync(CancellationToken ct);
}

/// <summary>Where a downloaded file was stored.</summary>
/// <param name="Path">Path inside the server (container).</param>
/// <param name="HostPath">The same file on the host, if media.hostDir is configured.</param>
public sealed record StoredMedia(string Path, string? HostPath);

/// <summary>New messages pushed by Telegram while the server runs (TDLib updateNewMessage).</summary>
public interface IInboundMessageSource
{
    /// <summary>Yields new messages as they arrive. Requires <see cref="ITelegramClient.EnsureReadyAsync"/> first.</summary>
    IAsyncEnumerable<TgMessage> ReadInboundAsync(CancellationToken ct);
}

/// <summary>Stores downloaded media in the media directory.</summary>
public interface IMediaStore
{
    /// <summary>Copies a downloaded file into the media store.</summary>
    Task<StoredMedia> StoreAsync(string sourcePath, long chatId, long messageId, string? fileName, CancellationToken ct);

    /// <summary>Deletes stored files older than <paramref name="retentionDays"/> days (0 = do nothing); returns how many. Must not throw.</summary>
    int Cleanup(int retentionDays);
}

/// <summary>Maps media paths between the container and the host (pure string mapping).</summary>
public static class MediaPaths
{
    /// <summary>
    /// The host path for <paramref name="containerPath"/> under <paramref name="mediaDir"/>, or null when no host
    /// directory is configured or the path is not inside the media directory.
    /// </summary>
    public static string? ToHostPath(string containerPath, string mediaDir, string? hostDir)
    {
        ArgumentNullException.ThrowIfNull(containerPath);
        ArgumentNullException.ThrowIfNull(mediaDir);
        if (string.IsNullOrWhiteSpace(hostDir)) return null;
        var rel = Path.GetRelativePath(mediaDir, containerPath);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
        return hostDir.TrimEnd('/') + "/" + rel.Replace('\\', '/');
    }
}
