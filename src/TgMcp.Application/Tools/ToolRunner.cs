using Microsoft.Extensions.Logging;
using TgMcp.Application.Audit;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>
/// Common pipeline for every tool call: rate limit, global gate (before touching Telegram), readiness,
/// folder resolution, the tool body, error mapping and exactly one audit record.
/// </summary>
public sealed partial class ToolRunner
{
    private readonly IPolicy _policy;
    private readonly ITelegramClient _client;
    private readonly IRateLimiter _rate;
    private readonly IAuditSink _audit;
    private readonly FolderGuard _folders;
    private readonly TimeProvider _time;
    private readonly ILogger<ToolRunner> _log;

    /// <summary>Creates the runner.</summary>
    public ToolRunner(IPolicy policy, ITelegramClient client, IRateLimiter rate, IAuditSink audit,
        FolderGuard folders, TimeProvider time, ILogger<ToolRunner> log)
    {
        _policy = policy;
        _client = client;
        _rate = rate;
        _audit = audit;
        _folders = folders;
        _time = time;
        _log = log;
    }

    /// <summary>Runs <paramref name="body"/> for tool <paramref name="tool"/> that needs <paramref name="globalNeeds"/>.</summary>
    public async Task<ToolResult> RunAsync(string tool, Need[] globalNeeds, Func<CancellationToken, Task<ToolOutcome>> body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        ToolOutcome outcome;
        if (!_rate.TryAcquire())
        {
            var reason = $"rate limit exceeded ({_policy.Config.RatePerMinute}/min)";
            outcome = new(ToolResult.Error(reason), null, 0, false, "limits", reason);
        }
        else if (_policy.CheckSwitches(globalNeeds) is { Allowed: false } g)
        {
            outcome = ToolOutcome.Denied(g, null);
        }
        else
        {
            outcome = await Execute(body, ct).ConfigureAwait(false);
        }
        _audit.Write(new AuditEntry(_time.GetUtcNow(), tool, outcome.ChatId, outcome.Count, outcome.Allowed, outcome.Rule, outcome.Reason));
        LogCall(tool, outcome.ChatId, outcome.Count, outcome.Allowed, outcome.Rule, outcome.Reason);
        return outcome.Result;
    }

    private async Task<ToolOutcome> Execute(Func<CancellationToken, Task<ToolOutcome>> body, CancellationToken ct)
    {
        try
        {
            await _client.EnsureReadyAsync(ct).ConfigureAwait(false);
            await _folders.EnsureAsync(ct).ConfigureAwait(false);
            return await body(ct).ConfigureAwait(false);
        }
        catch (TelegramUnavailableException e)
        {
            return new(ToolResult.Error($"Telegram unavailable: {e.Message}"), null, 0, false, "telegram", "unavailable: " + e.Message);
        }
        catch (TelegramCallException e)
        {
            return new(ToolResult.Error(e.Message), null, 0, true, "telegram", $"telegram error {e.Code}");
        }
        catch (ArgumentException e)
        {
            return new(ToolResult.Error($"bad argument: {e.Message}"), null, 0, true, "arguments", "bad argument");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(ToolResult.Error("local storage error"), null, 0, true, "storage", e.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "tool {Tool} chat={ChatId} count={Count} allowed={Allowed} rule={Rule} reason={Reason}")]
    private partial void LogCall(string tool, long? chatId, int count, bool allowed, string rule, string reason);
}
