using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgMcp.Application.Telegram;
using TgMcp.Domain;
using static TdLib.TdApi;
using static TdLib.TdApi.AuthorizationState;

namespace TgMcp.Infrastructure.Telegram;

/// <summary>
/// TDLib implementation of <see cref="ITelegramClient"/> for the MCP server. Starts TDLib lazily on the
/// first tool call, never prompts for login, and refuses to start when no session exists.
/// </summary>
public sealed class TdTelegramClient : ITelegramClient, IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

    private readonly TelegramOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger<TdTelegramClient> _log;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ConcurrentDictionary<long, (TgChat Chat, DateTimeOffset At)> _chats = new();
    private readonly ConcurrentDictionary<long, string> _userNames = new();
    private TdSession? _session;
    private long _myId;

    /// <summary>Creates the client; nothing is started until <see cref="EnsureReadyAsync"/>.</summary>
    public TdTelegramClient(IOptions<TelegramOptions> options, TimeProvider time, ILogger<TdTelegramClient> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        _o = options.Value;
        _time = time;
        _log = log;
    }

    private TdSession S => _session ?? throw new TelegramUnavailableException("Telegram session is not started");

    /// <inheritdoc />
    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_session?.State is AuthorizationStateReady) return;
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session?.State is AuthorizationStateReady) return;
            if (_o.Problem() is string problem) throw new TelegramUnavailableException(problem);
            if (!_o.HasSession()) throw new TelegramUnavailableException("not logged in: the owner must run `tgmcp login` first");
            if (_session is not null)
            {
                await _session.DisposeAsync().ConfigureAwait(false);
                _session = null;
            }
            var session = new TdSession(_o, _time, _log);
            var state = await session.WaitSettledAsync(StartTimeout, ct).ConfigureAwait(false);
            if (state is not AuthorizationStateReady)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw new TelegramUnavailableException(state is null
                    ? "TDLib did not start in time"
                    : $"not logged in ({state.GetType().Name}): the owner must run `tgmcp login`");
            }
            _session = session;
            var me = await session.CallAsync(new GetMe(), ct).ConfigureAwait(false);
            _myId = me.Id;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetChatIdsAsync(int limit, CancellationToken ct)
    {
        try
        {
            await S.CallAsync(new LoadChats { ChatList = new ChatList.ChatListMain(), Limit = limit }, ct).ConfigureAwait(false);
        }
        catch (TelegramCallException e) when (e.Code == 404)
        {
            // All chats are already loaded.
        }
        var chats = await S.CallAsync(new GetChats { ChatList = new ChatList.ChatListMain(), Limit = limit }, ct).ConfigureAwait(false);
        return chats.ChatIds ?? [];
    }

    /// <inheritdoc />
    public async Task<TgChat> GetChatAsync(long chatId, CancellationToken ct)
    {
        if (_chats.TryGetValue(chatId, out var hit) && _time.GetUtcNow() - hit.At < CacheTtl) return hit.Chat;
        var chat = await S.CallAsync(new GetChat { ChatId = chatId }, ct).ConfigureAwait(false);
        var result = await MapChat(chat, ct).ConfigureAwait(false);
        _chats[chatId] = (result, _time.GetUtcNow());
        return result;
    }

    /// <inheritdoc />
    public async Task<TgChat?> SearchPublicChatAsync(string username, CancellationToken ct)
    {
        try
        {
            var chat = await S.CallAsync(new SearchPublicChat { Username = username }, ct).ConfigureAwait(false);
            return await MapChat(chat, ct).ConfigureAwait(false);
        }
        catch (TelegramCallException e) when (e.Code == 400)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TgMessage>> GetChatHistoryAsync(long chatId, long fromMessageId, int limit, CancellationToken ct)
    {
        var r = await S.CallAsync(new GetChatHistory
        {
            ChatId = chatId, FromMessageId = fromMessageId, Offset = 0, Limit = limit, OnlyLocal = false,
        }, ct).ConfigureAwait(false);
        return await MapAll(r.Messages_, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TgMessage>> GetTopicHistoryAsync(long chatId, int topicId, long fromMessageId, int limit, CancellationToken ct)
    {
        var r = await S.CallAsync(new GetForumTopicHistory
        {
            ChatId = chatId, ForumTopicId = topicId, FromMessageId = fromMessageId, Offset = 0, Limit = limit,
        }, ct).ConfigureAwait(false);
        return await MapAll(r.Messages_, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TgMessage?> GetMessageAsync(long chatId, long messageId, CancellationToken ct)
    {
        try
        {
            var m = await S.CallAsync(new GetMessage { ChatId = chatId, MessageId = messageId }, ct).ConfigureAwait(false);
            return await MapMessage(m, ct).ConfigureAwait(false);
        }
        catch (TelegramCallException e) when (e.Code == 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TgMessage>> SearchChatMessagesAsync(long chatId, string query, long fromMessageId, int limit, CancellationToken ct)
    {
        var r = await S.CallAsync(new SearchChatMessages
        {
            ChatId = chatId, Query = query, FromMessageId = fromMessageId, Offset = 0, Limit = limit,
        }, ct).ConfigureAwait(false);
        return await MapAll(r.Messages, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TgPage> SearchMessagesAsync(string query, string offset, int limit, CancellationToken ct)
    {
        var r = await S.CallAsync(new SearchMessages
        {
            ChatList = null, Query = query, Offset = offset, Limit = limit, MinDate = 0, MaxDate = 0,
        }, ct).ConfigureAwait(false);
        return new TgPage(await MapAll(r.Messages, ct).ConfigureAwait(false), r.NextOffset ?? "");
    }

    /// <inheritdoc />
    public async Task<TgFile> DownloadFileAsync(int fileId, CancellationToken ct)
    {
        var f = await S.CallAsync(new DownloadFile { FileId = fileId, Priority = 1, Offset = 0, Limit = 0, Synchronous = true },
            ct, _o.DownloadTimeout).ConfigureAwait(false);
        return new TgFile(f.Id, f.Size > 0 ? f.Size : f.Local?.DownloadedSize ?? 0, f.Local?.Path, f.Local?.IsDownloadingCompleted == true);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlySet<long>>> GetFolderChatsAsync(CancellationToken ct)
    {
        var folders = S.Folders ?? throw new TelegramUnavailableException("chat folder list not received from Telegram yet");
        var result = new Dictionary<string, IReadOnlySet<long>>(StringComparer.Ordinal);
        foreach (var f in folders)
        {
            var list = new ChatList.ChatListFolder { ChatFolderId = f.Id };
            for (var i = 0; i < 50; i++)
            {
                try
                {
                    await S.CallAsync(new LoadChats { ChatList = list, Limit = 100 }, ct).ConfigureAwait(false);
                }
                catch (TelegramCallException e) when (e.Code == 404)
                {
                    break; // fully loaded
                }
            }
            var chats = await S.CallAsync(new GetChats { ChatList = list, Limit = 100_000 }, ct).ConfigureAwait(false);
            var title = f.Name?.Text?.Text ?? "";
            var ids = result.TryGetValue(title, out var existing) ? new HashSet<long>(existing) : [];
            ids.UnionWith(chats.ChatIds ?? []);
            result[title] = ids;
        }
        return result;
    }

    private async Task<TgChat> MapChat(Chat chat, CancellationToken ct)
    {
        switch (chat.Type)
        {
            case ChatType.ChatTypePrivate p:
                {
                    var user = await S.CallAsync(new GetUser { UserId = p.UserId }, ct).ConfigureAwait(false);
                    var kind = user.Type is UserType.UserTypeBot ? ChatKind.Bot : ChatKind.Private;
                    return new TgChat(chat.Id, chat.Title ?? "", Usernames(user.Usernames), kind);
                }
            case ChatType.ChatTypeSecret s:
                {
                    var user = await S.CallAsync(new GetUser { UserId = s.UserId }, ct).ConfigureAwait(false);
                    var kind = user.Type is UserType.UserTypeBot ? ChatKind.Bot : ChatKind.Private;
                    return new TgChat(chat.Id, chat.Title ?? "", Usernames(user.Usernames), kind);
                }
            case ChatType.ChatTypeSupergroup sg:
                {
                    var g = await S.CallAsync(new GetSupergroup { SupergroupId = sg.SupergroupId }, ct).ConfigureAwait(false);
                    return new TgChat(chat.Id, chat.Title ?? "", Usernames(g.Usernames), sg.IsChannel ? ChatKind.Channel : ChatKind.Group, g.IsForum);
                }
            default:
                return new TgChat(chat.Id, chat.Title ?? "", [], ChatKind.Group);
        }
    }

    private static string[] Usernames(Usernames? u) => u?.ActiveUsernames ?? [];

    private async Task<IReadOnlyList<TgMessage>> MapAll(Message[]? messages, CancellationToken ct)
    {
        var list = new List<TgMessage>(messages?.Length ?? 0);
        foreach (var m in messages ?? []) list.Add(await MapMessage(m, ct).ConfigureAwait(false));
        return list;
    }

    private async Task<TgMessage> MapMessage(Message m, CancellationToken ct)
    {
        var (text, media) = MessageContentMapper.Map(m.Content);
        var topic = m.TopicId is MessageTopic.MessageTopicForum f ? f.ForumTopicId : (int?)null;
        return new TgMessage(m.ChatId, m.Id, DateTimeOffset.FromUnixTimeSeconds(m.Date), await SenderName(m.SenderId, ct).ConfigureAwait(false),
            m.IsOutgoing || (m.SenderId is MessageSender.MessageSenderUser u && u.UserId == _myId), text, media, topic);
    }

    private async Task<string> SenderName(MessageSender? sender, CancellationToken ct)
    {
        switch (sender)
        {
            case MessageSender.MessageSenderUser u:
                if (_userNames.TryGetValue(u.UserId, out var cached)) return cached;
                try
                {
                    var user = await S.CallAsync(new GetUser { UserId = u.UserId }, ct).ConfigureAwait(false);
                    var name = $"{user.FirstName} {user.LastName}".Trim();
                    if (user.Usernames?.ActiveUsernames is [var first, ..]) name += $" (@{first})";
                    return _userNames[u.UserId] = name.Length > 0 ? name : $"user {u.UserId}";
                }
                catch (TelegramCallException)
                {
                    return $"user {u.UserId}";
                }
            case MessageSender.MessageSenderChat c:
                try
                {
                    return (await GetChatAsync(c.ChatId, ct).ConfigureAwait(false)).Title;
                }
                catch (TelegramCallException)
                {
                    return $"chat {c.ChatId}";
                }
            default:
                return "unknown";
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_session is not null) await _session.DisposeAsync().ConfigureAwait(false);
        _startGate.Dispose();
    }
}
