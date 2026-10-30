using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TgMcp.Application.Audit;
using TgMcp.Domain;

namespace TgMcp.Server.Hosting;

/// <summary>Writes the config sha256 and validation result to audit and stderr at start.</summary>
public sealed partial class StartupAudit : IHostedService
{
    private readonly IPolicy _policy;
    private readonly LoadedConfig _loaded;
    private readonly IAuditSink _audit;
    private readonly ILogger<StartupAudit> _log;

    /// <summary>Creates the service.</summary>
    public StartupAudit(IPolicy policy, LoadedConfig loaded, IAuditSink audit, ILogger<StartupAudit> log)
    {
        _policy = policy;
        _loaded = loaded;
        _audit = audit;
        _log = log;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var c = _policy.Config;
        var written = _audit.WriteEvent("start", new Dictionary<string, object?>
        {
            ["version"] = typeof(StartupAudit).Assembly.GetName().Version?.ToString(3),
            ["config_sha256"] = c.Sha256,
            ["config_file"] = _loaded.FilePath,
            ["config_file_found"] = _loaded.FileFound,
            ["config_valid"] = c.IsValid,
            ["enabled"] = c.Enabled,
            ["errors"] = c.Errors,
        });
        LogStart(c.Sha256, c.IsValid, c.Enabled);
        LogFile(_loaded.FilePath, _loaded.FileFound ? "loaded" : _loaded.Explicit ? "MISSING" : "not present, using environment only");
        if (!written) LogAuditUnavailable();
        foreach (var e in c.Errors) LogConfigError(e);
        if (!c.IsValid) LogDenyAll();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "tgmcp starting; config sha256={Sha} valid={Valid} enabled={Enabled}")]
    private partial void LogStart(string sha, bool valid, bool enabled);

    [LoggerMessage(Level = LogLevel.Information, Message = "config file {Path}: {State}")]
    private partial void LogFile(string path, string state);

    [LoggerMessage(Level = LogLevel.Error, Message = "config error: {Error}")]
    private partial void LogConfigError(string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "audit log is not writable: every tool call will be denied until it is")]
    private partial void LogAuditUnavailable();

    [LoggerMessage(Level = LogLevel.Error, Message = "configuration is invalid: every tool call will be denied")]
    private partial void LogDenyAll();
}
