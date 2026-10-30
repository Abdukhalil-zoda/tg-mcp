using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TgMcp.Application.Events;
using TgMcp.Application.Telegram;

namespace TgMcp.Server.Events;

/// <summary>
/// When events are enabled: starts TDLib eagerly (retrying while not logged in), waits for the MCP client,
/// runs catch-up, then forwards every new message to <see cref="InboundEventProcessor"/>.
/// </summary>
public sealed partial class ChannelEventService : BackgroundService
{
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);

    private readonly InboundEventProcessor _processor;
    private readonly ITelegramClient _telegram;
    private readonly IInboundMessageSource _inbound;
    private readonly IChannelNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<ChannelEventService> _log;

    /// <summary>Creates the service.</summary>
    public ChannelEventService(InboundEventProcessor processor, ITelegramClient telegram, IInboundMessageSource inbound,
        IChannelNotifier notifier, TimeProvider time, ILogger<ChannelEventService> log)
    {
        _processor = processor;
        _telegram = telegram;
        _inbound = inbound;
        _notifier = notifier;
        _time = time;
        _log = log;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_processor.Enabled) return;
        try
        {
            await StartTelegramAsync(stoppingToken).ConfigureAwait(false);
            await _notifier.WaitForClientAsync(stoppingToken).ConfigureAwait(false);
            LogCaughtUp(await _processor.CatchUpAsync(stoppingToken).ConfigureAwait(false));
            using var flush = new PeriodicTimer(FlushInterval, _time);
            var flusher = FlushLoop(flush, stoppingToken);
            await foreach (var m in _inbound.ReadInboundAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _processor.HandleAsync(m, catchUp: false, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is TelegramCallException or TelegramUnavailableException or IOException)
                {
                    LogEventFailed(e.Message);
                }
            }
            await flusher.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private async Task StartTelegramAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await _telegram.EnsureReadyAsync(ct).ConfigureAwait(false);
                LogStarted();
                return;
            }
            catch (TelegramUnavailableException e)
            {
                LogNotReady(e.Message);
                await Task.Delay(Retry, _time, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task FlushLoop(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await _processor.FlushIfDueAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "inbound events: Telegram started, listening for new messages")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Warning, Message = "inbound events: Telegram not ready ({Reason}); retrying in a minute")]
    private partial void LogNotReady(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "inbound events: catch-up emitted {Count} message(s)")]
    private partial void LogCaughtUp(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "inbound events: a message could not be processed: {Error}")]
    private partial void LogEventFailed(string error);
}
