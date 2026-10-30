using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TgMcp.Application.Audit;
using TgMcp.Server.Hosting;

namespace TgMcp.Infrastructure.Tests;

public class JsonlAuditSinkTests
{
    private static JsonlAuditSink Sink(string path) =>
        new(Options.Create(new ServerOptions { DataDir = "/data", AuditPath = path }), NullLogger<JsonlAuditSink>.Instance);

    private static AuditEntry Entry => new(DateTimeOffset.UnixEpoch, "read_messages", -1001234567890, 3, true, "chats[0] id=-1001234567890", "ok");

    [Fact]
    public void Writes_json_lines()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sub", "audit.jsonl");
        var sink = Sink(path);
        Assert.True(sink.IsAvailable());
        Assert.True(sink.Write(Entry));
        Assert.True(sink.WriteEvent("start", new Dictionary<string, object?> { ["config_sha256"] = "abc" }));
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"tool\":\"read_messages\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"chat\":-1001234567890", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"event\":\"start\"", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_failure_instead_of_throwing()
    {
        using var dir = new TempDir();
        var sink = Sink(dir.Path); // a directory cannot be appended to
        Assert.False(sink.IsAvailable());
        Assert.False(sink.Write(Entry));
        Assert.False(sink.WriteEvent("start", new Dictionary<string, object?>()));
    }
}
