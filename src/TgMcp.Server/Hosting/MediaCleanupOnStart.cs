using Microsoft.Extensions.Hosting;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Server.Hosting;

/// <summary>Applies Limits__MediaRetentionDays once at startup (skipped when the config is invalid).</summary>
public sealed class MediaCleanupOnStart : IHostedService
{
    private readonly IPolicy _policy;
    private readonly IMediaStore _store;

    /// <summary>Creates the service.</summary>
    public MediaCleanupOnStart(IPolicy policy, IMediaStore store)
    {
        _policy = policy;
        _store = store;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_policy.Config.IsValid) _store.Cleanup(_policy.Config.MediaRetentionDays);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
