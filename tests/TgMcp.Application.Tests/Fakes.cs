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

/// <summary>Wires the real use cases to fakes.</summary>
internal sealed class Harness
{
    public static readonly DateTimeOffset Now = new(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public FakeTelegram Telegram { get; } = new();
    public MemoryAudit Audit { get; } = new();
    public FakeTimeProvider Time { get; } = new(Now);
    public PolicyEngine Policy { get; }
    public ToolContext Context { get; }

    public Harness(params (string, string)[] env)
    {
        Policy = new PolicyEngine(PolicyConfig.FromPairs(env));
        var folders = new FolderGuard(Policy, Telegram, Time, NullLogger<FolderGuard>.Instance);
        var runner = new ToolRunner(Policy, Telegram, new TokenBucket(Policy.Config.RatePerMinute, Time), Audit, folders, Time,
            NullLogger<ToolRunner>.Instance);
        Context = new ToolContext(runner, Policy, new ChatResolver(Telegram), Telegram, Time);
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
    public SendMessageTool Send => new(Context, DraftStore, _sendLimiter ??= new PerChatRateLimiter(Policy.Config.SendsPerMinute, Time));
    public DraftCommands DraftCommands => new(DraftStore, Context, Audit);
    public FakeNotifier Notifier { get; } = new();
    public MemoryEventState EventState { get; } = new();
    private InboundEventProcessor? _events;
    public InboundEventProcessor Events => _events ??= new(Policy, Telegram, Notifier, Audit, EventState,
        new FolderGuard(Policy, Telegram, Time, NullLogger<FolderGuard>.Instance), Time, NullLogger<InboundEventProcessor>.Instance);
    public DownloadMediaTool Download => new(Context, Media);

    public AuditEntry LastAudit => Audit.Entries[^1];

    public static TgMessage Msg(long chat, long id, string text, double daysAgo = 0, int? topic = null, TgMedia? media = null) =>
        new(chat, id, Now.AddDays(-daysAgo), "Alice", false, text, media, topic);
}
