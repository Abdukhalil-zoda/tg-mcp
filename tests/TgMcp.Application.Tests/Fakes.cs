using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TgMcp.Application.Audit;
using TgMcp.Application.Drafts;
using TgMcp.Application.Events;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;

namespace TgMcp.Application.Tests;

internal sealed class FakeTelegram : ITelegramClient
{
    public Dictionary<long, TgChat> Chats { get; } = [];
    public Dictionary<long, List<TgMessage>> Messages { get; } = [];
    public Dictionary<string, IReadOnlySet<long>>? Folders { get; set; } = [];
    public string? Unavailable { get; set; }
    public int Calls { get; private set; }
    public int ReadyCalls { get; private set; }
    public List<int> Downloads { get; } = [];
    public long DownloadedSize { get; set; } = -1;

    public void Add(TgChat chat, params TgMessage[] messages)
    {
        Chats[chat.Id] = chat;
        Messages[chat.Id] = [.. messages.OrderByDescending(m => m.Id)];
    }

    public Task EnsureReadyAsync(CancellationToken ct)
    {
        ReadyCalls++;
        return Unavailable is null ? Task.CompletedTask : throw new TelegramUnavailableException(Unavailable);
    }

    public Task<IReadOnlyList<long>> GetChatIdsAsync(int limit, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<long>>([.. Chats.Keys.Take(limit)]);
    }

    public Task<TgChat> GetChatAsync(long chatId, CancellationToken ct)
    {
        Calls++;
        return Chats.TryGetValue(chatId, out var c) ? Task.FromResult(c) : throw new TelegramCallException(400, "Chat not found");
    }

    public Task<TgChat?> SearchPublicChatAsync(string username, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Chats.Values.FirstOrDefault(c => c.Usernames.Any(u => u.Equals(username, StringComparison.OrdinalIgnoreCase))));
    }

    private IEnumerable<TgMessage> From(long chatId, long from) =>
        Messages.GetValueOrDefault(chatId, []).Where(m => from == 0 || m.Id <= from);

    public Task<IReadOnlyList<TgMessage>> GetChatHistoryAsync(long chatId, long fromMessageId, int limit, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<TgMessage>>([.. From(chatId, fromMessageId).Take(limit)]);
    }

    public Task<IReadOnlyList<TgMessage>> GetTopicHistoryAsync(long chatId, int topicId, long fromMessageId, int limit, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<TgMessage>>([.. From(chatId, fromMessageId).Where(m => m.TopicId == topicId).Take(limit)]);
    }

    public Task<TgMessage?> GetMessageAsync(long chatId, long messageId, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Messages.GetValueOrDefault(chatId, []).FirstOrDefault(m => m.Id == messageId));
    }

    public Task<IReadOnlyList<TgMessage>> SearchChatMessagesAsync(long chatId, string query, long fromMessageId, int limit, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<TgMessage>>([.. From(chatId, fromMessageId).Where(m => m.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(limit)]);
    }

    public Task<TgPage> SearchMessagesAsync(string query, string offset, int limit, CancellationToken ct)
    {
        Calls++;
        var all = Messages.Values.SelectMany(x => x).Where(m => m.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Date).ToList();
        var skip = offset.Length == 0 ? 0 : int.Parse(offset, System.Globalization.CultureInfo.InvariantCulture);
        var page = all.Skip(skip).Take(limit).ToList();
        var next = skip + page.Count < all.Count ? (skip + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        return Task.FromResult(new TgPage(page, next));
    }

    public Task<TgFile> DownloadFileAsync(int fileId, CancellationToken ct)
    {
        Calls++;
        Downloads.Add(fileId);
        var media = Messages.Values.SelectMany(x => x).First(m => m.Media?.FileId == fileId).Media!;
        return Task.FromResult(new TgFile(fileId, DownloadedSize >= 0 ? DownloadedSize : media.Size, $"/tdlib/files/{fileId}", true));
    }

    public List<OutgoingMessage> Sent { get; } = [];
    public TelegramCallException? SendFailure { get; set; }

    public Task<TgSent> SendMessageAsync(OutgoingMessage message, CancellationToken ct)
    {
        Calls++;
        if (SendFailure is not null) throw SendFailure;
        Sent.Add(message);
        return Task.FromResult(new TgSent(message.ChatId, 1000 + Sent.Count, Harness.Now));
    }

    public TgReactionOptions ReactionOptions { get; set; } = new(["👍", "❤", "🔥"], ["🤩"], [], null);
    public List<(long Chat, long Message, string Emoji, bool Added)> Reactions { get; } = [];

    public Task<TgReactionOptions> GetReactionOptionsAsync(long chatId, long messageId, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(ReactionOptions);
    }

    public Task AddReactionAsync(long chatId, long messageId, string emoji, CancellationToken ct)
    {
        Calls++;
        Reactions.Add((chatId, messageId, emoji, true));
        return Task.CompletedTask;
    }

    public Task RemoveReactionAsync(long chatId, long messageId, string emoji, CancellationToken ct)
    {
        Calls++;
        Reactions.Add((chatId, messageId, emoji, false));
        return Task.CompletedTask;
    }

    public Dictionary<string, TgChat> PhoneUsers { get; } = [];
    public List<(string Phone, string Name)> Imports { get; } = [];
    public HashSet<string> LocalContacts { get; } = [];

    public Task<TgPhoneChat?> ResolvePhoneAsync(string e164, string contactName, CancellationToken ct)
    {
        Calls++;
        var known = LocalContacts.Contains(e164);
        if (!known) Imports.Add((e164, contactName));
        if (!PhoneUsers.TryGetValue(e164, out var chat)) return Task.FromResult<TgPhoneChat?>(null);
        Chats[chat.Id] = chat;
        return Task.FromResult<TgPhoneChat?>(new TgPhoneChat(chat, !known));
    }

    public TelegramCallException? AiFailure { get; set; }
    public List<(string Kind, long Chat, long Message, string? Lang)> AiCalls { get; } = [];

    public Task<string> SummarizeMessageAsync(long chatId, long messageId, string? translateTo, CancellationToken ct)
    {
        Calls++;
        if (AiFailure is not null) throw AiFailure;
        AiCalls.Add(("summary", chatId, messageId, translateTo));
        return Task.FromResult("short summary");
    }

    public Task<string> TranslateMessageAsync(long chatId, long messageId, string toLanguage, CancellationToken ct)
    {
        Calls++;
        if (AiFailure is not null) throw AiFailure;
        AiCalls.Add(("translation", chatId, messageId, toLanguage));
        return Task.FromResult("translated text");
    }

    public List<(long Chat, long Message, string Text)> Edits { get; } = [];
    public List<(long Chat, long[] Ids, bool ForEveryone)> Deletes { get; } = [];

    public Task EditMessageTextAsync(long chatId, long messageId, string text, CancellationToken ct)
    {
        Calls++;
        Edits.Add((chatId, messageId, text));
        return Task.CompletedTask;
    }

    public Task DeleteMessagesAsync(long chatId, IReadOnlyList<long> messageIds, bool forEveryone, CancellationToken ct)
    {
        Calls++;
        Deletes.Add((chatId, [.. messageIds], forEveryone));
        return Task.CompletedTask;
    }

    public HashSet<(long Chat, long Message)> Protected { get; } = [];
    public HashSet<(long Chat, long Message)> NotEditable { get; } = [];
    public HashSet<(long Chat, long Message)> NotRevocable { get; } = [];
    public List<(long From, long[] Ids, long To, int? Topic, bool Copy)> Forwards { get; } = [];
    private long _nextForwardId = 9000;

    public Task<TgMessageProperties> GetMessagePropertiesAsync(long chatId, long messageId, CancellationToken ct)
    {
        Calls++;
        var key = (chatId, messageId);
        return Task.FromResult(new TgMessageProperties(!Protected.Contains(key), !NotEditable.Contains(key), !NotRevocable.Contains(key), true));
    }

    public Task<IReadOnlyList<long>> ForwardMessagesAsync(long fromChatId, IReadOnlyList<long> messageIds, long toChatId, int? toTopicId, bool sendCopy, CancellationToken ct)
    {
        Calls++;
        Forwards.Add((fromChatId, [.. messageIds], toChatId, toTopicId, sendCopy));
        return Task.FromResult<IReadOnlyList<long>>([.. messageIds.Select(_ => ++_nextForwardId)]);
    }

    public List<(long Chat, long? UpTo)> MarkedRead { get; } = [];

    public Task<long?> MarkReadAsync(long chatId, long? upToMessageId, CancellationToken ct)
    {
        Calls++;
        var upTo = upToMessageId ?? Messages.GetValueOrDefault(chatId, []).Select(m => (long?)m.Id).FirstOrDefault();
        MarkedRead.Add((chatId, upToMessageId));
        return Task.FromResult(upTo);
    }

    public Dictionary<string, TgChatPreview> Previews { get; } = [];
    public List<JoinTarget> Joins { get; } = [];
    public string JoinStatus { get; set; } = "joined";

    public Task<TgChatPreview> PreviewJoinAsync(JoinTarget target, CancellationToken ct)
    {
        Calls++;
        return Previews.TryGetValue(target.Value, out var p) ? Task.FromResult(p) : throw new ArgumentException("no public chat with that username", nameof(target));
    }

    public Task<TgJoinResult> JoinAsync(JoinTarget target, CancellationToken ct)
    {
        Calls++;
        Joins.Add(target);
        var p = Previews[target.Value];
        return Task.FromResult(JoinStatus == "requested" ? new TgJoinResult("requested", null, "") : new TgJoinResult(JoinStatus, p.ChatId ?? -100777, p.Title));
    }

    public List<TgPublicChat> PublicChats { get; } = [];
    public List<string> PublicQueries { get; } = [];

    public Task<IReadOnlyList<TgPublicChat>> SearchPublicChatsAsync(string query, int limit, CancellationToken ct)
    {
        Calls++;
        PublicQueries.Add(query);
        return Task.FromResult<IReadOnlyList<TgPublicChat>>([.. PublicChats.Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || c.Usernames.Any(u => u.Contains(query.TrimStart('@'), StringComparison.OrdinalIgnoreCase))).Take(limit)]);
    }

    public Task<TgPublicChat?> FindPublicChatAsync(string username, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(PublicChats.FirstOrDefault(c => c.Usernames.Any(u => u.Equals(username, StringComparison.OrdinalIgnoreCase))));
    }

    public List<TgDirectoryChat> Directory { get; } = [];
    public int? LastScanMax { get; private set; }

    public Task<IReadOnlyList<TgDirectoryChat>> ScanGroupsAsync(int maxChats, CancellationToken ct)
    {
        Calls++;
        LastScanMax = maxChats;
        return Task.FromResult<IReadOnlyList<TgDirectoryChat>>([.. Directory.Take(maxChats)]);
    }

    public Dictionary<long, TgUser> Users { get; } = [];
    public List<(long Chat, long User)> Added { get; } = [];
    public TelegramCallException? AddFailure { get; set; }

    public Task<TgUser?> GetUserAsync(long userId, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Users.GetValueOrDefault(userId));
    }

    public Task<TgUser?> FindUserAsync(string username, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Users.Values.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)));
    }

    public Task AddChatMemberAsync(long chatId, long userId, CancellationToken ct)
    {
        Calls++;
        if (AddFailure is not null) throw AddFailure;
        Added.Add((chatId, userId));
        return Task.CompletedTask;
    }

    public Dictionary<int, (string Name, long[] Chats)> CreatedFolders { get; } = [];
    public List<int> DeletedFolders { get; } = [];
    public HashSet<long> NotLinkable { get; } = [];
    public List<(int Folder, long[] Chats, string Link)> FolderLinks { get; } = [];
    public TelegramCallException? CreateFolderFailure { get; set; }
    public int FailCreateFolderAfter { get; set; } = int.MaxValue;
    private int _nextFolder = 10;

    public Task<int> CreateChatFolderAsync(string name, IReadOnlyList<long> chatIds, CancellationToken ct)
    {
        Calls++;
        if (CreateFolderFailure is not null && CreatedFolders.Count >= FailCreateFolderAfter) throw CreateFolderFailure;
        var id = ++_nextFolder;
        CreatedFolders[id] = (name, [.. chatIds]);
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<long>> GetChatsForFolderLinkAsync(int folderId, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<long>>([.. CreatedFolders[folderId].Chats.Where(c => !NotLinkable.Contains(c))]);
    }

    public Task<string> CreateFolderLinkAsync(int folderId, IReadOnlyList<long> chatIds, CancellationToken ct)
    {
        Calls++;
        var link = $"https://t.me/addlist/SecretSlug{folderId}";
        FolderLinks.Add((folderId, [.. chatIds], link));
        return Task.FromResult(link);
    }

    public Task DeleteChatFolderAsync(int folderId, CancellationToken ct)
    {
        Calls++;
        DeletedFolders.Add(folderId);
        return Task.CompletedTask;
    }

    public Dictionary<string, TgFolderPreview> FolderPreviews { get; } = [];
    public List<(string Slug, long[] Chats)> JoinedFolders { get; } = [];
    public TelegramCallException? AddFolderFailure { get; set; }

    public Task<TgFolderPreview> CheckFolderLinkAsync(FolderLink link, CancellationToken ct)
    {
        Calls++;
        return FolderPreviews.TryGetValue(link.Slug, out var p) ? Task.FromResult(p) : throw new TelegramCallException(400, "INVITE_SLUG_EXPIRED");
    }

    public Task AddFolderByLinkAsync(FolderLink link, IReadOnlyList<long> chatIds, CancellationToken ct)
    {
        Calls++;
        if (AddFolderFailure is not null) throw AddFolderFailure;
        JoinedFolders.Add((link.Slug, [.. chatIds]));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, IReadOnlySet<long>>> GetFolderChatsAsync(CancellationToken ct)
    {
        Calls++;
        return Folders is null
            ? throw new TelegramUnavailableException("folders unknown")
            : Task.FromResult<IReadOnlyDictionary<string, IReadOnlySet<long>>>(Folders);
    }
}

internal sealed class MemoryAudit : IAuditSink
{
    public List<AuditEntry> Entries { get; } = [];
    public bool Available { get; set; } = true;
    public bool FailWrites { get; set; }

    public bool IsAvailable() => Available;

    public bool Write(AuditEntry entry)
    {
        if (FailWrites) return false;
        Entries.Add(entry);
        return true;
    }

    public bool WriteEvent(string name, IReadOnlyDictionary<string, object?> fields) => !FailWrites;
}

internal sealed class MemoryMediaStore : IMediaStore
{
    public string? HostDir { get; set; }
    public List<int> Cleanups { get; } = [];

    public int Cleanup(int retentionDays)
    {
        Cleanups.Add(retentionDays);
        return 0;
    }

    public Task<StoredMedia> StoreAsync(string sourcePath, long chatId, long messageId, string? fileName, CancellationToken ct)
    {
        var path = $"/data/media/{chatId}_{messageId}_{fileName ?? "file"}";
        return Task.FromResult(new StoredMedia(path, MediaPaths.ToHostPath(path, "/data/media", HostDir)));
    }
}

internal sealed class MemoryDraftStore : IDraftStore
{
    private int _next;
    public Dictionary<string, Draft> Drafts { get; } = [];

    public Task<Draft> SaveAsync(string chatTitle, OutgoingMessage message, CancellationToken ct)
    {
        var d = new Draft($"{++_next:x8}", Harness.Now, chatTitle, message);
        Drafts[d.Id] = d;
        return Task.FromResult(d);
    }

    public Task<Draft> SaveJoinAsync(string chatTitle, PendingJoin join, CancellationToken ct)
    {
        var d = new Draft($"{++_next:x8}", Harness.Now, chatTitle, null, Draft.JoinKind, join);
        Drafts[d.Id] = d;
        return Task.FromResult(d);
    }

    public Task<Draft> SavePhoneAsync(PendingPhoneMessage message, CancellationToken ct)
    {
        var d = new Draft($"{++_next:x8}", Harness.Now, message.ContactName, null, Draft.PhoneKind, Phone: message);
        Drafts[d.Id] = d;
        return Task.FromResult(d);
    }

    public Task<Draft> SaveEditAsync(string chatTitle, PendingEdit edit, CancellationToken ct)
    {
        var d = new Draft($"{++_next:x8}", Harness.Now, chatTitle, null, Draft.EditKind, Edit: edit);
        Drafts[d.Id] = d;
        return Task.FromResult(d);
    }

    public Task<Draft> SaveDeleteAsync(string chatTitle, PendingDelete delete, CancellationToken ct)
    {
        var d = new Draft($"{++_next:x8}", Harness.Now, chatTitle, null, Draft.DeleteKind, Delete: delete);
        Drafts[d.Id] = d;
        return Task.FromResult(d);
    }

    public Task<Draft> SaveForwardAsync(string chatTitle, PendingForward forward, CancellationToken ct)
    {
        var d = new Draft($"{++_next:x8}", Harness.Now, chatTitle, null, Draft.ForwardKind, Forward: forward);
        Drafts[d.Id] = d;
        return Task.FromResult(d);
    }

    public Task<Draft> SaveReactionAsync(string chatTitle, PendingReaction reaction, CancellationToken ct)
    {
        var d = new Draft($"{++_next:x8}", Harness.Now, chatTitle, null, Draft.ReactionKind, Reaction: reaction);
        Drafts[d.Id] = d;
        return Task.FromResult(d);
    }

    public Task<Draft> SaveAddMemberAsync(string chatTitle, PendingAddMember add, CancellationToken ct)
    {
        var d = new Draft($"{++_next:x8}", Harness.Now, chatTitle, null, Draft.AddMemberKind, AddMember: add);
        Drafts[d.Id] = d;
        return Task.FromResult(d);
    }

    public Task<Draft> SaveShareFolderAsync(PendingShareFolder share, CancellationToken ct)
    {
        var d = new Draft($"{++_next:x8}", Harness.Now, share.Name, null, Draft.ShareFolderKind, ShareFolder: share);
        Drafts[d.Id] = d;
        return Task.FromResult(d);
    }

    public Task<Draft> SaveJoinFolderAsync(PendingJoinFolder join, CancellationToken ct)
    {
        var d = new Draft($"{++_next:x8}", Harness.Now, join.FolderName, null, Draft.JoinFolderKind, JoinFolder: join);
        Drafts[d.Id] = d;
        return Task.FromResult(d);
    }

    public Task<IReadOnlyList<Draft>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Draft>>([.. Drafts.Values]);
    public Task<Draft?> GetAsync(string id, CancellationToken ct) => Task.FromResult(Drafts.GetValueOrDefault(id));
    public Task<bool> DeleteAsync(string id, CancellationToken ct) => Task.FromResult(Drafts.Remove(id));
}

internal sealed class FakeNotifier : IChannelNotifier
{
    public List<ChannelEvent> Events { get; } = [];
    public Task WaitForClientAsync(CancellationToken ct) => Task.CompletedTask;

    public Task NotifyAsync(ChannelEvent channelEvent, CancellationToken ct)
    {
        Events.Add(channelEvent);
        return Task.CompletedTask;
    }
}

internal sealed class MemoryEventState : IEventStateStore
{
    public Dictionary<long, long> Saved { get; private set; } = [];
    public Task<Dictionary<long, long>> LoadAsync(CancellationToken ct) => Task.FromResult(new Dictionary<long, long>(Saved));

    public Task SaveAsync(IReadOnlyDictionary<long, long> lastDelivered, CancellationToken ct)
    {
        Saved = new Dictionary<long, long>(lastDelivered);
        return Task.CompletedTask;
    }
}

internal sealed class MemorySentLog : ISentLog
{
    public HashSet<(long Chat, long Message)> Entries { get; } = [];

    public Task RecordAsync(long chatId, long messageId, CancellationToken ct)
    {
        Entries.Add((chatId, messageId));
        return Task.CompletedTask;
    }

    public Task<bool> ContainsAsync(long chatId, long messageId, CancellationToken ct) => Task.FromResult(Entries.Contains((chatId, messageId)));

    public Task RemoveAsync(long chatId, IReadOnlyCollection<long> messageIds, CancellationToken ct)
    {
        foreach (var id in messageIds) Entries.Remove((chatId, id));
        return Task.CompletedTask;
    }
}

/// <summary>Wires the real use cases to fakes.</summary>
internal sealed class Harness
{
    public static readonly DateTimeOffset Now = new(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public FakeTelegram Telegram { get; } = new();
    public MemoryAudit Audit { get; } = new();
    public MemorySentLog SentLog { get; } = new();
    public FakeTimeProvider Time { get; } = new(Now);
    public PolicyEngine Policy { get; }
    public ToolContext Context { get; }

    public Harness(params (string, string)[] env)
    {
        Policy = new PolicyEngine(PolicyConfig.FromPairs(env));
        var folders = new FolderGuard(Policy, Telegram, Time, NullLogger<FolderGuard>.Instance);
        var runner = new ToolRunner(Policy, Telegram, new TokenBucket(Policy.Config.RatePerMinute, Time), Audit, folders, Time,
            NullLogger<ToolRunner>.Instance);
        Context = new ToolContext(runner, Policy, new ChatResolver(Telegram), Telegram, Time, SentLog);
    }

    public ListChatsTool ListChats => new(Context);
    public ReadMessagesTool Read => new(Context);
    public ReadTopicTool Topic => new(Context);
    public GetMessageTool Get => new(Context);
    public SearchChatTool SearchChat => new(Context);
    public SearchAllTool SearchAll => new(Context);
    public MemoryMediaStore Media { get; } = new();
    public MemoryDraftStore DraftStore { get; } = new();
    private ISendLimiter? _sendLimiter;
    private INewChatLimiter? _newChats;
    public SendMessageTool Send => new(Context, DraftStore, _sendLimiter ??= new PerChatRateLimiter(Policy.Config.SendsPerMinute, Time),
        _newChats ??= new NewChatRateLimiter(Policy.Config.NewChatsPerDay, Time));
    public DraftCommands DraftCommands => new(DraftStore, Context, Audit);
    private IJoinLimiter? _joinLimiter;
    public JoinChatTool Join => new(Context, DraftStore, _joinLimiter ??= new JoinRateLimiter(Policy.Config.JoinsPerHour, Policy.Config.JoinsPerDay, Time));
    private IAddMemberLimiter? _addLimiter;
    public AddMemberTool AddMember => new(Context, DraftStore, _addLimiter ??= new AddMemberRateLimiter(Policy.Config.AddsPerHour, Policy.Config.AddsPerDay, Time));
    private IPublicSearchLimiter? _publicLimiter;
    public SearchPublicTool SearchPublic => new(Context, _publicLimiter ??= new PublicSearchRateLimiter(Policy.Config.PublicSearchesPerHour, Time));
    public FindChatsTool FindChats => new(Context);
    public ShareFolderTool ShareFolder => new(Context, DraftStore);
    public JoinFolderTool JoinFolder => new(Context, DraftStore, _joinLimiter ??= new JoinRateLimiter(Policy.Config.JoinsPerHour, Policy.Config.JoinsPerDay, Time));
    public PreviewChatTool Preview => new(Context);
    public MarkReadTool MarkRead => new(Context);
    public EditMessageTool Edit => new(Context, DraftStore);
    public AiTools Ai => new(Context);
    public DeleteMessageTool Delete => new(Context, DraftStore);
    public ForwardTool Forward => new(Context, DraftStore, _sendLimiter ??= new PerChatRateLimiter(Policy.Config.SendsPerMinute, Time));
    public ReactTool React => new(Context, DraftStore, _sendLimiter ??= new PerChatRateLimiter(Policy.Config.SendsPerMinute, Time));
    public FakeNotifier Notifier { get; } = new();
    public MemoryEventState EventState { get; } = new();
    private InboundEventProcessor? _events;
    public InboundEventProcessor Events => _events ??= new(Policy, Telegram, Notifier, Audit, EventState,
        new FolderGuard(Policy, Telegram, Time, NullLogger<FolderGuard>.Instance), Time, NullLogger<InboundEventProcessor>.Instance);
    public DownloadMediaTool Download => new(Context, Media);

    public AuditEntry LastAudit => Audit.Entries[^1];

    public static TgMessage Msg(long chat, long id, string text, double daysAgo = 0, int? topic = null, TgMedia? media = null, bool fromMe = false) =>
        new(chat, id, Now.AddDays(-daysAgo), "Alice", fromMe, text, media, topic);
}
