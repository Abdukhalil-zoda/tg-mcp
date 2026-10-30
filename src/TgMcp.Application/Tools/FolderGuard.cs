using Microsoft.Extensions.Logging;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>
/// Resolves chat folders for <c>Deny__N__Folder</c> rules: on first use and then at most every
/// <see cref="RefreshInterval"/>. Any failure fails closed (the policy then denies everything).
/// </summary>
public sealed partial class FolderGuard
{
    /// <summary>How long a resolved folder membership is trusted.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly IPolicy _policy;
    private readonly ITelegramClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger<FolderGuard> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _resolvedAt = DateTimeOffset.MinValue;

    /// <summary>Creates the guard.</summary>
    public FolderGuard(IPolicy policy, ITelegramClient client, TimeProvider time, ILogger<FolderGuard> log)
    {
        _policy = policy;
        _client = client;
        _time = time;
        _log = log;
    }

    /// <summary>Makes sure folder membership is fresh, if any folder deny exists.</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        if (!_policy.NeedsFolders) return;
        if (_policy.FoldersResolved && _time.GetUtcNow() - _resolvedAt < RefreshInterval) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_policy.FoldersResolved && _time.GetUtcNow() - _resolvedAt < RefreshInterval) return;
            try
            {
                _policy.SetFolders(await _client.GetFolderChatsAsync(ct).ConfigureAwait(false));
                _resolvedAt = _time.GetUtcNow();
            }
            catch (Exception e) when (e is TelegramCallException or TelegramUnavailableException or InvalidOperationException)
            {
                _policy.SetFolderError(e.Message);
                LogFailed(e.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "chat folders could not be resolved, denying everything: {Reason}")]
    private partial void LogFailed(string reason);
}
