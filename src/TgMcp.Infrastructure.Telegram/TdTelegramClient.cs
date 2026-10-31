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
public sealed class TdTelegramClient : ITelegramClient, IInboundMessageSource, IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SendConfirmTimeout = TimeSpan.FromSeconds(60);

    private readonly TelegramOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger<TdTelegramClient> _log;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ConcurrentDictionary<long, (TgChat Chat, DateTimeOffset At)> _chats = new();
    private readonly ConcurrentDictionary<long, (string Name, string? Username, bool IsBot)> _users = new();
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
            if (session.ParametersError is string paramsError)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw new TelegramUnavailableException(DatabaseProblem(paramsError));
            }
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

    /// <summary>Turns a TDLib parameters error into an owner-facing explanation.</summary>
    public static string DatabaseProblem(string tdError) =>
        tdError.Contains("lock", StringComparison.OrdinalIgnoreCase) || tdError.Contains("already in use", StringComparison.OrdinalIgnoreCase)
            ? "the TDLib database is in use by another tgmcp process for this data directory (only one may run at a time: stop or disconnect the MCP server before `tgmcp login` / `tgmcp approve`, or the other way round)"
            : $"TDLib could not open its database: {tdError}";

    /// <inheritdoc />
    public async Task<TgReactionOptions> GetReactionOptionsAsync(long chatId, long messageId, CancellationToken ct)
    {
        var available = await S.CallAsync(new GetMessageAvailableReactions { ChatId = chatId, MessageId = messageId, RowSize = 8 }, ct).ConfigureAwait(false);
        var all = (available.TopReactions ?? []).Concat(available.RecentReactions ?? []).Concat(available.PopularReactions ?? [])
            .Where(r => r.Type is ReactionType.ReactionTypeEmoji).ToList();
        string E(AvailableReaction r) => ((ReactionType.ReactionTypeEmoji)r.Type).Emoji;
        var free = all.Where(r => !r.NeedsPremium).Select(E).Distinct().ToList();
        var premium = all.Where(r => r.NeedsPremium).Select(E).Except(free).Distinct().ToList();
        var message = await S.CallAsync(new GetMessage { ChatId = chatId, MessageId = messageId }, ct).ConfigureAwait(false);
        var chosen = (message.InteractionInfo?.Reactions?.Reactions ?? [])
            .Where(r => r.IsChosen && r.Type is ReactionType.ReactionTypeEmoji)
            .Select(r => ((ReactionType.ReactionTypeEmoji)r.Type).Emoji).ToList();
        var unavailable = available.UnavailabilityReason?.GetType().Name;
        return new TgReactionOptions(free, premium, chosen, unavailable);
    }

    /// <inheritdoc />
    public Task AddReactionAsync(long chatId, long messageId, string emoji, CancellationToken ct) =>
        S.CallAsync(new AddMessageReaction
        {
            ChatId = chatId, MessageId = messageId, ReactionType = new ReactionType.ReactionTypeEmoji { Emoji = emoji },
            IsBig = false, UpdateRecentReactions = false,
        }, ct);

    /// <inheritdoc />
    public Task RemoveReactionAsync(long chatId, long messageId, string emoji, CancellationToken ct) =>
        S.CallAsync(new RemoveMessageReaction
        {
            ChatId = chatId, MessageId = messageId, ReactionType = new ReactionType.ReactionTypeEmoji { Emoji = emoji },
        }, ct);

    /// <inheritdoc />
    public async Task<long?> MarkReadAsync(long chatId, long? upToMessageId, CancellationToken ct)
    {
        var upTo = upToMessageId;
        if (upTo is null)
        {
            var chat = await S.CallAsync(new GetChat { ChatId = chatId }, ct).ConfigureAwait(false);
            upTo = chat.LastMessage?.Id;
        }
        if (upTo is long id)
        {
            await S.CallAsync(new ViewMessages
            {
                ChatId = chatId,
                MessageIds = [id],
                Source = new MessageSource.MessageSourceChatHistory(),
                ForceRead = true,
            }, ct).ConfigureAwait(false);
        }
        if (upToMessageId is null)
        {
            await S.CallAsync(new ReadAllChatMentions { ChatId = chatId }, ct).ConfigureAwait(false);
            await S.CallAsync(new ReadAllChatReactions { ChatId = chatId }, ct).ConfigureAwait(false);
        }
        return upTo;
    }

    /// <inheritdoc />
    public async Task<TgChatPreview> PreviewJoinAsync(JoinTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == JoinTargetKind.Invite)
        {
            var info = await S.CallAsync(new CheckChatInviteLink { InviteLink = target.Canonical }, ct).ConfigureAwait(false);
            var (type, kind) = info.Type switch
            {
                InviteLinkChatType.InviteLinkChatTypeChannel => ("channel", ChatKind.Channel),
                InviteLinkChatType.InviteLinkChatTypeSupergroup => ("supergroup", ChatKind.Group),
                _ => ("group", ChatKind.Group),
            };
            long? id = info.ChatId != 0 ? info.ChatId : null;
            var member = false;
            if (id is long known)
            {
                try
                {
                    member = await IsMemberAsync(await S.CallAsync(new GetChat { ChatId = known }, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                }
                catch (TelegramCallException)
                {
                    member = false;
                }
            }
            return new TgChatPreview(id, info.Title ?? "", type, kind, [], info.MemberCount, info.CreatesJoinRequest, member);
        }

        Chat chat;
        try
        {
            chat = await S.CallAsync(new SearchPublicChat { Username = target.Value }, ct).ConfigureAwait(false);
        }
        catch (TelegramCallException e) when (e.Code == 400)
        {
            throw new ArgumentException("no public chat with that username", nameof(target));
        }
        var mapped = await MapChat(chat, ct).ConfigureAwait(false);
        if (chat.Type is ChatType.ChatTypeSupergroup sg)
        {
            var g = await S.CallAsync(new GetSupergroup { SupergroupId = sg.SupergroupId }, ct).ConfigureAwait(false);
            return new TgChatPreview(chat.Id, mapped.Title, sg.IsChannel ? "channel" : "supergroup", mapped.Kind, mapped.Usernames,
                g.MemberCount, g.JoinByRequest, IsMember(g.Status));
        }
        var other = mapped.Kind == ChatKind.Bot ? "bot" : mapped.Kind == ChatKind.Private ? "private" : "group";
        return new TgChatPreview(chat.Id, mapped.Title, other, mapped.Kind, mapped.Usernames, 0, false, false);
    }

    /// <inheritdoc />
    public async Task<TgJoinResult> JoinAsync(JoinTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        long? knownId = null;
        var title = "";
        ChatJoinResult result;
        try
        {
            if (target.Kind == JoinTargetKind.Invite)
            {
                result = await S.CallAsync(new JoinChatByInviteLink { InviteLink = target.Canonical }, ct).ConfigureAwait(false);
            }
            else
            {
                var pub = await S.CallAsync(new SearchPublicChat { Username = target.Value }, ct).ConfigureAwait(false);
                knownId = pub.Id;
                title = pub.Title ?? "";
                result = await S.CallAsync(new JoinChat { ChatId = pub.Id }, ct).ConfigureAwait(false);
            }
        }
        catch (TelegramCallException e) when (e.TdMessage.Contains("INVITE_REQUEST_SENT", StringComparison.OrdinalIgnoreCase))
        {
            return new TgJoinResult("requested", knownId, title);
        }
        switch (result)
        {
            case ChatJoinResult.ChatJoinResultSuccess ok:
                if (title.Length == 0)
                {
                    try
                    {
                        title = (await S.CallAsync(new GetChat { ChatId = ok.ChatId }, ct).ConfigureAwait(false)).Title ?? "";
                    }
                    catch (TelegramCallException)
                    {
                        // title is optional
                    }
                }
                return new TgJoinResult("joined", ok.ChatId, title);
            case ChatJoinResult.ChatJoinResultRequestSent:
                return new TgJoinResult("requested", knownId, title);
            case ChatJoinResult.ChatJoinResultGuardBotApprovalRequired:
                throw new TelegramCallException(403, "this chat requires approval from a guard bot (Web App); join it manually in Telegram");
            case ChatJoinResult.ChatJoinResultDeclined:
                throw new TelegramCallException(403, "the join was declined by the chat's guard bot");
            default:
                return new TgJoinResult("joined", knownId, title);
        }
    }

    private async Task<bool> IsMemberAsync(Chat chat, CancellationToken ct)
    {
        switch (chat.Type)
        {
            case ChatType.ChatTypeSupergroup sg:
                return IsMember((await S.CallAsync(new GetSupergroup { SupergroupId = sg.SupergroupId }, ct).ConfigureAwait(false)).Status);
            case ChatType.ChatTypeBasicGroup bg:
                return IsMember((await S.CallAsync(new GetBasicGroup { BasicGroupId = bg.BasicGroupId }, ct).ConfigureAwait(false)).Status);
            default:
                return false;
        }
    }

    private static bool IsMember(ChatMemberStatus? status) => status is ChatMemberStatus.ChatMemberStatusMember
        or ChatMemberStatus.ChatMemberStatusAdministrator or ChatMemberStatus.ChatMemberStatusCreator
        or ChatMemberStatus.ChatMemberStatusRestricted { IsMember: true };

    /// <inheritdoc />
    public async Task<TgSent> SendMessageAsync(OutgoingMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var request = new SendMessage
        {
            ChatId = message.ChatId,
            TopicId = message.TopicId is int t ? new MessageTopic.MessageTopicForum { ForumTopicId = t } : null,
            ReplyTo = message.ReplyToMessageId > 0 ? new InputMessageReplyTo.InputMessageReplyToMessage { MessageId = message.ReplyToMessageId } : null,
            Options = new MessageSendOptions { DisableNotification = message.DisableNotification },
            InputMessageContent = new InputMessageContent.InputMessageText
            {
                Text = new FormattedText { Text = message.Text, Entities = [] },
                LinkPreviewOptions = new LinkPreviewOptions { IsDisabled = true },
                ClearDraft = false,
            },
        };
        var sent = await S.SendAndConfirmAsync(request, SendConfirmTimeout, ct).ConfigureAwait(false);
        return new TgSent(sent.ChatId, sent.Id, DateTimeOffset.FromUnixTimeSeconds(sent.Date));
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
        var (name, username, isBot, senderId) = await Sender(m.SenderId, ct).ConfigureAwait(false);
        var replyTo = m.ReplyTo is MessageReplyTo.MessageReplyToMessage r && (r.ChatId == 0 || r.ChatId == m.ChatId) ? r.MessageId : (long?)null;
        var display = username is null ? name : $"{name} (@{username})";
        return new TgMessage(m.ChatId, m.Id, DateTimeOffset.FromUnixTimeSeconds(m.Date), display,
            m.IsOutgoing || (m.SenderId is MessageSender.MessageSenderUser u && u.UserId == _myId), text, media, topic,
            senderId, username, isBot, replyTo);
    }

    private async Task<(string Name, string? Username, bool IsBot, long? Id)> Sender(MessageSender? sender, CancellationToken ct)
    {
        switch (sender)
        {
            case MessageSender.MessageSenderUser u:
                if (_users.TryGetValue(u.UserId, out var cached)) return (cached.Name, cached.Username, cached.IsBot, u.UserId);
                try
                {
                    var user = await S.CallAsync(new GetUser { UserId = u.UserId }, ct).ConfigureAwait(false);
                    var name = $"{user.FirstName} {user.LastName}".Trim();
                    var entry = (name.Length > 0 ? name : $"user {u.UserId}", user.Usernames?.ActiveUsernames is [var first, ..] ? first : null,
                        user.Type is UserType.UserTypeBot);
                    _users[u.UserId] = entry;
                    return (entry.Item1, entry.Item2, entry.Item3, u.UserId);
                }
                catch (TelegramCallException)
                {
                    return ($"user {u.UserId}", null, false, u.UserId);
                }
            case MessageSender.MessageSenderChat c:
                try
                {
                    var chat = await GetChatAsync(c.ChatId, ct).ConfigureAwait(false);
                    return (chat.Title, chat.Usernames is [var first, ..] ? first : null, false, c.ChatId);
                }
                catch (TelegramCallException)
                {
                    return ($"chat {c.ChatId}", null, false, c.ChatId);
                }
            default:
                return ("unknown", null, false, null);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TgMessage> ReadInboundAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var m in S.NewMessages.ReadAllAsync(ct).ConfigureAwait(false))
        {
            TgMessage? mapped;
            try
            {
                mapped = await MapMessage(m, ct).ConfigureAwait(false);
            }
            catch (TelegramCallException)
            {
                mapped = null; // skip messages that cannot be mapped
            }
            if (mapped is not null) yield return mapped;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_session is not null) await _session.DisposeAsync().ConfigureAwait(false);
        _startGate.Dispose();
    }
}
