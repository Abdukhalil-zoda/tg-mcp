using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgMcp.Application.Audit;

namespace TgMcp.Server.Hosting;

/// <summary>Appends audit records as JSON lines. Write failures are logged to stderr and never thrown.</summary>
public sealed partial class JsonlAuditSink : IAuditSink
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly ILogger<JsonlAuditSink> _log;
    private readonly Lock _lock = new();

    /// <summary>Creates the sink for <see cref="ServerOptions.EffectiveAuditPath"/>.</summary>
    public JsonlAuditSink(IOptions<ServerOptions> options, ILogger<JsonlAuditSink> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = options.Value.EffectiveAuditPath;
        _log = log;
    }

    /// <inheritdoc />
    public void Write(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Append(new Dictionary<string, object?>
        {
            ["ts"] = entry.TimeUtc.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["tool"] = entry.Tool,
            ["chat"] = entry.ChatId,
            ["count"] = entry.Count,
            ["allowed"] = entry.Allowed,
            ["rule"] = entry.Rule,
            ["reason"] = entry.Reason,
        });
    }

    /// <inheritdoc />
    public void WriteEvent(string name, IReadOnlyDictionary<string, object?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var row = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["event"] = name,
        };
        foreach (var (k, v) in fields) row[k] = v;
        Append(row);
    }

    private void Append(Dictionary<string, object?> row)
    {
        var line = JsonSerializer.Serialize(row, Json) + "\n";
        try
        {
            lock (_lock)
            {
                if (Path.GetDirectoryName(_path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                File.AppendAllText(_path, line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogFailed(_path, e.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "audit write to {Path} failed: {Error}")]
    private partial void LogFailed(string path, string error);
}
