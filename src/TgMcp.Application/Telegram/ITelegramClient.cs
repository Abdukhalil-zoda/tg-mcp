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
public sealed record TgMessage(
    long ChatId,
    long Id,
    DateTimeOffset Date,
    string Sender,
    bool FromMe,
    string Text,
    TgMedia? Media,
    int? TopicId)
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

    /// <summary>Chat folder title to the ids of the chats currently in that folder.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlySet<long>>> GetFolderChatsAsync(CancellationToken ct);
}

/// <summary>Where a downloaded file was stored.</summary>
/// <param name="Path">Path inside the server (container).</param>
/// <param name="HostPath">The same file on the host, if media.hostDir is configured.</param>
public sealed record StoredMedia(string Path, string? HostPath);

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
