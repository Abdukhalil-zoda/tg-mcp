using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using TgMcp.Application.Events;
using TgMcp.Server.Events;
using TgMcp.Server.Mcp;

namespace TgMcp.Infrastructure.Tests;

/// <summary>Runs a real SDK McpServer over in-memory pipes and checks what goes over the wire.</summary>
public sealed class ChannelNotifierTests : IAsyncDisposable
{
    private readonly Pipe _toServer = new();
    private readonly Pipe _fromServer = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly StreamReader _reader;
    private McpServer? _server;
    private Task? _run;

    public ChannelNotifierTests() => _reader = new StreamReader(_fromServer.Reader.AsStream(), Encoding.UTF8);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<JsonElement> StartAsync(bool channel)
    {
        var options = new McpServerOptions();
        McpSetup.Configure(options, channel);
        var transport = new StreamServerTransport(_toServer.Reader.AsStream(), _fromServer.Writer.AsStream(), "tgmcp-test");
        _server = McpServer.Create(transport, options);
        _run = _server.RunAsync(_cts.Token);
        await SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""");
        var init = await NextAsync();
        await SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        return init;
    }

    private async Task SendAsync(string line)
    {
        await _toServer.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), Ct);
        await _toServer.Writer.FlushAsync(Ct);
    }

    private async Task<JsonElement> NextAsync()
    {
        var line = await _reader.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.NotNull(line);
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    [Fact]
    public async Task Initialize_declares_the_channel_capability_and_instructions()
    {
        var init = await StartAsync(channel: true);
        var result = init.GetProperty("result");
        Assert.Equal(JsonValueKind.Object, result.GetProperty("capabilities").GetProperty("experimental").GetProperty("claude/channel").ValueKind);
        Assert.False(result.GetProperty("capabilities").GetProperty("experimental").TryGetProperty("claude/channel/permission", out _));
        Assert.Contains("untrusted third-party data", result.GetProperty("instructions").GetString(), StringComparison.Ordinal);
        Assert.Equal("tgmcp", result.GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Without_events_no_channel_capability_is_declared()
    {
        var init = await StartAsync(channel: false);
        Assert.False(init.GetProperty("result").GetProperty("capabilities").TryGetProperty("experimental", out _));
    }

    [Fact]
    public async Task Notifier_sends_a_channel_notification_through_the_sdk()
    {
        await StartAsync(channel: true);
        var notifier = new McpChannelNotifier(() => _server!, new FakeTimeProvider());
        await notifier.WaitForClientAsync(Ct);
        var meta = new Dictionary<string, string> { ["chat_id"] = "-1001234567890", ["message_id"] = "5" };
        await notifier.NotifyAsync(new ChannelEvent(EventFormatter.Marker + "\nhello", meta), Ct);

        var n = await NextAsync();
        Assert.Equal("notifications/claude/channel", n.GetProperty("method").GetString());
        Assert.False(n.TryGetProperty("id", out _));
        var p = n.GetProperty("params");
        Assert.Equal(EventFormatter.Marker + "\nhello", p.GetProperty("content").GetString());
        Assert.Equal("-1001234567890", p.GetProperty("meta").GetProperty("chat_id").GetString());
        Assert.Equal("5", p.GetProperty("meta").GetProperty("message_id").GetString());
    }

    [Fact]
    public async Task The_stdio_host_registers_the_session_the_notifier_resolves()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);
        services.AddMcpServer(o => McpSetup.Configure(o, true)).WithStreamServerTransport(_toServer.Reader.AsStream(), _fromServer.Writer.AsStream());
        await using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        Assert.NotNull(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<McpServer>(sp));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _toServer.Writer.CompleteAsync();
        if (_run is not null)
        {
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException)
            {
                // stopped
            }
        }
        if (_server is not null) await _server.DisposeAsync();
        _cts.Dispose();
        _reader.Dispose();
    }
}
