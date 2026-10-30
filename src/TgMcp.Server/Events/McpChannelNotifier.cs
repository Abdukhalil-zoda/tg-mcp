using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using TgMcp.Application.Events;

namespace TgMcp.Server.Events;

/// <summary>
/// Sends <c>notifications/claude/channel</c> through the SDK's <see cref="McpServer"/> session
/// (<see cref="ModelContextProtocol.McpSession.SendNotificationAsync{TParams}(string, TParams, System.Text.Json.JsonSerializerOptions?, CancellationToken)"/>).
/// </summary>
public sealed class McpChannelNotifier : IChannelNotifier
{
    /// <summary>The channel notification method understood by Claude Code.</summary>
    public const string Method = "notifications/claude/channel";

    /// <summary>Experimental capability key declared in initialize.</summary>
    public const string Capability = "claude/channel";

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);
    private readonly Func<McpServer> _server;
    private readonly TimeProvider _time;

    /// <summary>Resolves the session lazily (it is created by the stdio host).</summary>
    public McpChannelNotifier(IServiceProvider services, TimeProvider time)
        : this(services.GetRequiredService<McpServer>, time)
    {
    }

    /// <summary>For tests: a fixed session.</summary>
    public McpChannelNotifier(Func<McpServer> server, TimeProvider time)
    {
        _server = server;
        _time = time;
    }

    /// <inheritdoc />
    public async Task WaitForClientAsync(CancellationToken ct)
    {
        while (_server().ClientInfo is null) await Task.Delay(Poll, _time, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task NotifyAsync(ChannelEvent channelEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channelEvent);
        var payload = new ChannelParams(channelEvent.Content, channelEvent.Meta);
        return _server().SendNotificationAsync(Method, payload, cancellationToken: ct);
    }

    /// <summary>Wire shape: <c>{ "content": "...", "meta": { "k": "v" } }</c>.</summary>
    /// <param name="Content">Event text.</param>
    /// <param name="Meta">String attributes.</param>
    public sealed record ChannelParams(
        [property: System.Text.Json.Serialization.JsonPropertyName("content")] string Content,
        [property: System.Text.Json.Serialization.JsonPropertyName("meta")] IReadOnlyDictionary<string, string> Meta);
}
