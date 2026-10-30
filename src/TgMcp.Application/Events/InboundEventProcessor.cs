using Microsoft.Extensions.Logging;
using TgMcp.Application.Audit;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;

namespace TgMcp.Application.Events;

/// <summary>What happened to one inbound message.</summary>
public enum EventOutcome
{
    /// <summary>Delivered to the client.</summary>
    Emitted,
    /// <summary>Not eligible (events off, policy, outgoing, duplicate).</summary>
    Skipped,
    /// <summary>Eligible but over events.maxPerMinute; counted for the summary.</summary>
    Dropped,
    /// <summary>Not delivered because the audit record could not be written (fail closed).</summary>
    AuditFailed,
}

/// <summary>
/// Turns inbound messages into channel events. An event is emitted only if events are enabled, the message
/// is incoming (or includeOutgoing), the chat is not denied, read is allowed and the effective rule has notify.
/// Bot/channel chats and bot senders need an explicit chat rule (defaults.notify is not enough).
/// Thread-safe; one instance per process.
/// </summary>
public sealed partial class InboundEventProcessor
{
    /// <summary>Length of the drop-summary window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Catch-up never emits more than this many messages.</summary>
    public const int CatchUpCap = 50;

    private readonly IPolicy _policy;
    private readonly ITelegramClient _telegram;
    private readonly IChannelNotifier _notifier;
    private readonly IAuditSink _audit;
    private readonly IEventStateStore _state;
    private readonly FolderGuard _folders;
    private readonly TimeProvider _time;
    private readonly ILogger<InboundEventProcessor> _log;
    private readonly TokenBucket _bucket;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<long, long>? _last;
    private int _dropped;
    private DateTimeOffset? _firstDrop;

    /// <summary>Creates the processor.</summary>
    public InboundEventProcessor(IPolicy policy, ITelegramClient telegram, IChannelNotifier notifier, IAuditSink audit,
        IEventStateStore state, FolderGuard folders, TimeProvider time, ILogger<InboundEventProcessor> log)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
        _telegram = telegram;
        _notifier = notifier;
        _audit = audit;
        _state = state;
        _folders = folders;
        _time = time;
        _log = log;
        _bucket = new TokenBucket(policy.Config.EventsMaxPerMinute, time);
    }

    private PolicyConfig Config => _policy.Config;

    /// <summary>True if events are configured on (and the config is valid).</summary>
    public bool Enabled => Config.IsValid && Config.EventsEnabled;

    /// <summary>Handles one message; <paramref name="catchUp"/> marks messages replayed at start.</summary>
    public async Task<EventOutcome> HandleAsync(TgMessage message, bool catchUp, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Enabled) return EventOutcome.Skipped;
            if (message.FromMe && !Config.EventsIncludeOutgoing) return EventOutcome.Skipped;
            var last = await LastAsync(ct).ConfigureAwait(false);
            if (last.TryGetValue(message.ChatId, out var seen) && message.Id <= seen) return EventOutcome.Skipped;

            await _folders.EnsureAsync(ct).ConfigureAwait(false);
            var chat = await _telegram.GetChatAsync(message.ChatId, ct).ConfigureAwait(false);
            var d = Eligible(chat, message.SenderIsBot);
            if (d is null) return EventOutcome.Skipped;
            if (!d.DateOk(message.Date, _time.GetUtcNow()) || !d.TopicOk(message.TopicId)) return EventOutcome.Skipped;

            if (!_bucket.TryAcquire())
            {
                _dropped++;
                _firstDrop ??= _time.GetUtcNow();
                return EventOutcome.Dropped;
            }
            await FlushIfDueCoreAsync(ct).ConfigureAwait(false);

            var write = _policy.Check(chat.Info, Need.Write);
            var ev = EventFormatter.Build(message, chat, write.Allowed ? write.Write : WriteMode.Off, Config.EventsMaxTextChars, catchUp);
            var entry = new AuditEntry(_time.GetUtcNow(), catchUp ? "event_catch_up" : "event", chat.Id, 1, true, d.Rule,
                $"message {message.Id}, length {message.Text.Length}");
            if (!_audit.Write(entry))
            {
                LogAuditFailed(chat.Id);
                return EventOutcome.AuditFailed;
            }
            await _notifier.NotifyAsync(ev, ct).ConfigureAwait(false);
            last[chat.Id] = message.Id;
            await _state.SaveAsync(last, ct).ConfigureAwait(false);
            return EventOutcome.Emitted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Emits the drop summary if a window has passed since the first dropped message.</summary>
    public async Task FlushIfDueAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FlushIfDueCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// On start: emits messages from notify chats received in the last events.catchUpMinutes and newer than the
    /// last delivered id, oldest first, at most <see cref="CatchUpCap"/> (the newest ones).
    /// </summary>
    public async Task<int> CatchUpAsync(CancellationToken ct)
    {
        if (!Enabled || Config.EventsCatchUpMinutes == 0) return 0;
        var cutoff = _time.GetUtcNow() - TimeSpan.FromMinutes(Config.EventsCatchUpMinutes);
        await _folders.EnsureAsync(ct).ConfigureAwait(false);
        var last = await LastAsync(ct).ConfigureAwait(false);
        var found = new List<TgMessage>();
        foreach (var id in await _telegram.GetChatIdsAsync(200, ct).ConfigureAwait(false))
        {
            var chat = await _telegram.GetChatAsync(id, ct).ConfigureAwait(false);
            if (Eligible(chat, senderIsBot: false) is null) continue;
            var seen = last.GetValueOrDefault(id);
            foreach (var m in await _telegram.GetChatHistoryAsync(id, 0, CatchUpCap, ct).ConfigureAwait(false))
            {
                if (m.Date < cutoff || m.Id <= seen) break;
                found.Add(m);
            }
        }
        var emitted = 0;
        foreach (var m in found.OrderBy(m => m.Date).ThenBy(m => m.Id).TakeLast(CatchUpCap))
        {
            if (await HandleAsync(m, catchUp: true, ct).ConfigureAwait(false) == EventOutcome.Emitted) emitted++;
        }
        return emitted;
    }

    /// <summary>The read decision if this chat may produce events, else null.</summary>
    private Decision? Eligible(TgChat chat, bool senderIsBot)
    {
        var d = _policy.Check(chat.Info, Need.Read);
        if (!d.Allowed || !d.Notify) return null;
        var needsExplicitRule = chat.Kind is ChatKind.Bot or ChatKind.Channel || senderIsBot;
        return needsExplicitRule && d.Source != DecisionSource.ChatRule ? null : d;
    }

    private async Task FlushIfDueCoreAsync(CancellationToken ct)
    {
        if (_dropped == 0 || _firstDrop is not DateTimeOffset first || _time.GetUtcNow() - first < Window) return;
        var count = _dropped;
        _dropped = 0;
        _firstDrop = null;
        if (!_audit.Write(new AuditEntry(_time.GetUtcNow(), "event_dropped", null, count, false, "events", "rate limit (events.maxPerMinute)")))
        {
            LogAuditFailed(null);
            return;
        }
        await _notifier.NotifyAsync(EventFormatter.Dropped(count, Config.EventsMaxPerMinute), ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<long, long>> LastAsync(CancellationToken ct) =>
        _last ??= await _state.LoadAsync(ct).ConfigureAwait(false);

    [LoggerMessage(Level = LogLevel.Error, Message = "audit write failed; event for chat {ChatId} not delivered")]
    private partial void LogAuditFailed(long? chatId);
}
