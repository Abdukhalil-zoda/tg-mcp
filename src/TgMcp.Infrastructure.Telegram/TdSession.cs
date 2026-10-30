using System.Reflection;
using Microsoft.Extensions.Logging;
using TdLib;
using TdLib.Bindings;
using static TdLib.TdApi;
using static TdLib.TdApi.AuthorizationState;

namespace TgMcp.Telegram;

public sealed class TelegramOptions
{
    public int ApiId { get; init; }
    public string ApiHash { get; init; } = "";
    /// <summary>TDLib database encryption key (never logged).</summary>
    public byte[] DatabaseKey { get; init; } = [];
    public string DataDir { get; init; } = "/data";
    public string DatabaseDir => Path.Combine(DataDir, "tdlib");
    public string FilesDir => Path.Combine(DataDir, "files");
    public string MediaDir => Path.Combine(DataDir, "media");
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Null if usable, else why not (no values included).</summary>
    public string? Problem()
    {
        if (ApiId <= 0 || string.IsNullOrWhiteSpace(ApiHash)) return "TGMCP_ApiId/TGMCP_ApiHash not set (owner must provide api.env)";
        if (DatabaseKey.Length == 0) return "TGMCP_DatabaseKey / TGMCP_DatabaseKeyFile not set";
        return null;
    }

    /// <summary>Reads TGMCP_ApiId, TGMCP_ApiHash, TGMCP_DatabaseKey or TGMCP_DatabaseKeyFile, TGMCP_DataDir.</summary>
    public static TelegramOptions FromValues(Func<string, string?> get)
    {
        _ = int.TryParse(get("ApiId"), out var apiId);
        var key = get("DatabaseKey");
        byte[] keyBytes = [];
        if (!string.IsNullOrEmpty(key)) keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        else if (get("DatabaseKeyFile") is { Length: > 0 } path && File.Exists(path))
            keyBytes = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim());
        return new TelegramOptions
        {
            ApiId = apiId,
            ApiHash = get("ApiHash") ?? "",
            DatabaseKey = keyBytes,
            DataDir = get("DataDir") is { Length: > 0 } d ? d : "/data",
        };
    }
}

/// <summary>
/// Owns one TdClient: low log verbosity (stderr only), automatic SetTdlibParameters, authorization-state
/// tracking and chat-folder tracking from the single update stream. Requests map to Task&lt;T&gt; via
/// TdClient.ExecuteAsync, wrapped with timeout + FLOOD_WAIT retry.
/// </summary>
public sealed class TdSession : IAsyncDisposable
{
    private readonly TelegramOptions _o;
    private readonly ILogger _log;
    private readonly TdClient _client;
    private readonly Lock _lock = new();
    private AuthorizationState? _state;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile ChatFolderInfo[] _folders = [];
    private volatile bool _foldersKnown;

    public TdSession(TelegramOptions options, ILogger log)
    {
        _o = options;
        _log = log;
        var bindings = Interop.AutoDetectBindings();
        bindings.SetLogVerbosityLevel(TdLogLevel.Error); // 1; TDLib logs to stderr, never stdout
        _client = new TdClient(bindings);
        _client.Execute(new SetLogVerbosityLevel { NewVerbosityLevel = 1 });
        _client.UpdateReceived += OnUpdate;
    }

    public AuthorizationState? State { get { lock (_lock) return _state; } }
    public IReadOnlyList<ChatFolderInfo> Folders => _folders;
    public bool FoldersKnown => _foldersKnown;

    private void OnUpdate(object? sender, Update u)
    {
        switch (u)
        {
            case Update.UpdateAuthorizationState a:
                SetState(a.AuthorizationState);
                break;
            case Update.UpdateChatFolders f:
                _folders = f.ChatFolders ?? [];
                _foldersKnown = true;
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
        _log.LogInformation("tdlib authorization state: {State}", s.GetType().Name);
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
                DatabaseEncryptionKey = _o.DatabaseKey,
                UseFileDatabase = true,
                UseChatInfoDatabase = true,
                UseMessageDatabase = true,
                UseSecretChats = false,
                ApiId = _o.ApiId,
                ApiHash = _o.ApiHash,
                SystemLanguageCode = "en",
                DeviceModel = "TgMcp",
                SystemVersion = "Linux",
                ApplicationVersion = "TgMcp " + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0"),
            }).ConfigureAwait(false);
        }
        catch (TdException e)
        {
            _log.LogError("SetTdlibParameters failed: {Code} {Message}", e.Error?.Code, e.Error?.Message);
        }
    }

    /// <summary>Waits until the authorization state is no longer WaitTdlibParameters/null (or timeout).</summary>
    public async Task<AuthorizationState?> WaitSettledAsync(TimeSpan timeout, CancellationToken ct)
    {
        // Kick the client so it emits its first update, and learn the current state.
        try { SetStateIfNone(await _client.ExecuteAsync(new GetAuthorizationState()).WaitAsync(timeout, ct).ConfigureAwait(false)); }
        catch (TdException) { }
        catch (TimeoutException) { return State; }
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_state is not null and not AuthorizationStateWaitTdlibParameters) return _state;
                wait = _changed.Task;
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return State;
            try { await wait.WaitAsync(left, ct).ConfigureAwait(false); }
            catch (TimeoutException) { return State; }
        }
    }

    /// <summary>Waits for the next authorization state change (login flow).</summary>
    public async Task<AuthorizationState?> WaitChangeAsync(AuthorizationState? from, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (!ReferenceEquals(_state, from) && _state is not AuthorizationStateWaitTdlibParameters) return _state;
                wait = _changed.Task;
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return State;
            try { await wait.WaitAsync(left, ct).ConfigureAwait(false); }
            catch (TimeoutException) { return State; }
        }
    }

    private void SetStateIfNone(AuthorizationState s)
    {
        bool set;
        lock (_lock) set = _state is null;
        if (set) SetState(s);
    }

    /// <summary>One request with timeout and bounded FLOOD_WAIT retry. TDLib errors become TelegramCallException.</summary>
    public Task<T> CallAsync<T>(Function<T> f, CancellationToken ct) where T : TdApi.Object =>
        Retry.RunAsync(async _ =>
        {
            try { return await _client.ExecuteAsync(f).ConfigureAwait(false); }
            catch (TdException e) { throw new TelegramCallException(e.Error?.Code ?? 0, e.Error?.Message ?? "unknown"); }
        }, new RetryOptions(_o.RequestTimeout), ct);

    public async ValueTask DisposeAsync()
    {
        _client.UpdateReceived -= OnUpdate;
        try
        {
            if (State is not (null or AuthorizationStateClosed))
                await _client.ExecuteAsync(new Close()).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception) { /* closing anyway */ }
        _client.Dispose();
    }
}
