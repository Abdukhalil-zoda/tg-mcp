using System.Reflection;
using Microsoft.Extensions.Logging;
using TdLib;
using TdLib.Bindings;
using TgMcp.Application.Telegram;
using static TdLib.TdApi;
using static TdLib.TdApi.AuthorizationState;

namespace TgMcp.Infrastructure.Telegram;

/// <summary>
/// Owns one TdClient: low log verbosity (TDLib logs go to stderr, never stdout), automatic
/// SetTdlibParameters, and authorization-state and chat-folder tracking from the single update stream.
/// Requests map to <see cref="Task{T}"/> through TdClient.ExecuteAsync, wrapped with timeout and
/// bounded FLOOD_WAIT retry.
/// </summary>
public sealed partial class TdSession : IAsyncDisposable
{
    private readonly TelegramOptions _o;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly TdClient _client;
    private readonly Lock _lock = new();
    private AuthorizationState? _state;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ChatFolderInfo[] _folders = [];
    private bool _foldersKnown;
    private string? _parametersError;
    private readonly System.Threading.Channels.Channel<Message> _newMessages =
        System.Threading.Channels.Channel.CreateBounded<Message>(new System.Threading.Channels.BoundedChannelOptions(1000)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(long Chat, long OldId), TaskCompletionSource<Message>> _sends = new();

    /// <summary>Creates the TDLib client (no network traffic until the first request).</summary>
    public TdSession(TelegramOptions options, TimeProvider time, ILogger log)
    {
        _o = options ?? throw new ArgumentNullException(nameof(options));
        _time = time;
        _log = log;
        var bindings = Interop.AutoDetectBindings();
        bindings.SetLogVerbosityLevel(TdLogLevel.Error); // level 1, before the client exists
        _client = new TdClient(bindings);
        _client.Execute(new SetLogVerbosityLevel { NewVerbosityLevel = 1 });
        _client.UpdateReceived += OnUpdate;
    }

    /// <summary>Latest authorization state (null before the first update).</summary>
    public AuthorizationState? State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>New messages from updateNewMessage (bounded; the oldest are dropped if nobody reads).</summary>
    public System.Threading.Channels.ChannelReader<Message> NewMessages => _newMessages.Reader;

    /// <summary>Why SetTdlibParameters failed (e.g. the database is locked by another process), or null.</summary>
    public string? ParametersError
    {
        get { lock (_lock) return _parametersError; }
    }

    /// <summary>Chat folders from the last updateChatFolders, or null if none was received yet.</summary>
    public IReadOnlyList<ChatFolderInfo>? Folders
    {
        get { lock (_lock) return _foldersKnown ? _folders : null; }
    }

    private void OnUpdate(object? sender, Update u)
    {
        if (InboundRouter.ForEvents(u) is { } inbound) _newMessages.Writer.TryWrite(inbound);
        switch (u)
        {
            case Update.UpdateAuthorizationState a:
                SetState(a.AuthorizationState);
                break;
            case Update.UpdateMessageSendSucceeded ok:
                Pending(ok.Message.ChatId, ok.OldMessageId).TrySetResult(ok.Message);
                break;
            case Update.UpdateMessageSendFailed failed:
                Pending(failed.Message.ChatId, failed.OldMessageId).TrySetException(
                    new TelegramCallException(failed.Error?.Code ?? 0, failed.Error?.Message ?? "send failed"));
                break;
            case Update.UpdateChatFolders f:
                lock (_lock)
                {
                    _folders = f.ChatFolders ?? [];
                    _foldersKnown = true;
                }
                break;
        }
    }

    private void SetState(AuthorizationState s)
    {
        TaskCompletionSource old;
        lock (_lock)
        {
            _state = s;
            old = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        LogState(s.GetType().Name);
        if (s is AuthorizationStateWaitTdlibParameters) _ = SendParametersAsync();
        old.TrySetResult();
    }

    private async Task SendParametersAsync()
    {
        try
        {
            Directory.CreateDirectory(_o.DatabaseDir);
            Directory.CreateDirectory(_o.FilesDir);
            await _client.ExecuteAsync(new SetTdlibParameters
            {
                UseTestDc = false,
                DatabaseDirectory = _o.DatabaseDir,
                FilesDirectory = _o.FilesDir,
                DatabaseEncryptionKey = _o.ResolveDatabaseKey(),
                UseFileDatabase = true,
                UseChatInfoDatabase = true,
                UseMessageDatabase = true,
                UseSecretChats = false,
                ApiId = _o.ParsedApiId,
                ApiHash = _o.ApiHash,
                SystemLanguageCode = "en",
                DeviceModel = "TgMcp",
                SystemVersion = "Linux",
                ApplicationVersion = "TgMcp " + (typeof(TdSession).Assembly.GetName().Version?.ToString(3) ?? "0"),
            }).ConfigureAwait(false);
        }
        catch (TdException e)
        {
            LogParamsFailed(e.Error?.Code ?? 0, e.Error?.Message ?? "unknown");
            ReportParametersError(e.Error?.Message ?? "unknown");
        }
        catch (IOException e)
        {
            LogParamsFailed(0, e.Message);
            ReportParametersError(e.Message);
        }
    }

    private void ReportParametersError(string message)
    {
        TaskCompletionSource old;
        lock (_lock)
        {
            _parametersError = message;
            old = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    private TaskCompletionSource<Message> Pending(long chatId, long oldId) =>
        _sends.GetOrAdd((chatId, oldId), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>
    /// Sends a message and waits for updateMessageSendSucceeded/Failed for its temporary id.
    /// Times out with a clear error (the message may still be delivered later).
    /// </summary>
    public async Task<Message> SendAndConfirmAsync(SendMessage request, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var temp = await CallAsync(request, ct).ConfigureAwait(false);
        if (temp.SendingState is null) return temp; // already final
        if (temp.SendingState is MessageSendingState.MessageSendingStateFailed f)
            throw new TelegramCallException(f.Error?.Code ?? 0, f.Error?.Message ?? "send failed");
        var waiter = Pending(temp.ChatId, temp.Id);
        try
        {
            return await waiter.Task.WaitAsync(timeout, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TelegramCallException(408,
                $"the message was queued but Telegram did not confirm it within {timeout.TotalSeconds:0}s; check the chat before retrying to avoid a duplicate");
        }
        finally
        {
            _sends.TryRemove((temp.ChatId, temp.Id), out _);
        }
    }

    /// <summary>Activates the client and waits until it leaves WaitTdlibParameters (or the timeout passes).</summary>
    public async Task<AuthorizationState?> WaitSettledAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var current = await _client.ExecuteAsync(new GetAuthorizationState()).WaitAsync(timeout, _time, ct).ConfigureAwait(false);
            bool none;
            lock (_lock) none = _state is null;
            if (none) SetState(current);
        }
        catch (TdException)
        {
            // The update stream will deliver the state.
        }
        catch (TimeoutException)
        {
            return State;
        }
        return await WaitChangeAsync(null, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Waits until the state differs from <paramref name="from"/> and is not WaitTdlibParameters.</summary>
    public async Task<AuthorizationState?> WaitChangeAsync(AuthorizationState? from, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + timeout;
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_parametersError is not null) return _state;
                if (_state is not null && !ReferenceEquals(_state, from) && _state is not AuthorizationStateWaitTdlibParameters) return _state;
                wait = _changed.Task;
            }
            var left = deadline - _time.GetUtcNow();
            if (left <= TimeSpan.Zero) return State;
            try
            {
                await wait.WaitAsync(left, _time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return State;
            }
        }
    }

    /// <summary>One request with timeout and bounded FLOOD_WAIT retry; TDLib errors become <see cref="TelegramCallException"/>.</summary>
    public Task<T> CallAsync<T>(Function<T> function, CancellationToken ct, TimeSpan? timeout = null) where T : TdApi.Object =>
        Retry.RunAsync(async _ =>
        {
            try
            {
                return await _client.ExecuteAsync(function).ConfigureAwait(false);
            }
            catch (TdException e)
            {
                throw new TelegramCallException(e.Error?.Code ?? 0, e.Error?.Message ?? "unknown");
            }
        }, new RetryOptions(timeout ?? _o.RequestTimeout), _time, ct);

    /// <summary>Closes TDLib gracefully and releases the native client.</summary>
    public async ValueTask DisposeAsync()
    {
        _client.UpdateReceived -= OnUpdate;
        try
        {
            if (State is not (null or AuthorizationStateClosed))
                await _client.ExecuteAsync(new Close()).WaitAsync(TimeSpan.FromSeconds(5), _time).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TdException or TimeoutException)
        {
            // Closing anyway.
        }
        _client.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "TDLib authorization state: {State}")]
    private partial void LogState(string state);

    [LoggerMessage(Level = LogLevel.Error, Message = "SetTdlibParameters failed: {Code} {Message}")]
    private partial void LogParamsFailed(int code, string message);
}
