namespace TgMcp.Application.Events;

/// <summary>One event for the MCP client (Claude Code renders it as a channel message).</summary>
/// <param name="Content">Text body; starts with <see cref="EventFormatter.Marker"/> for messages.</param>
/// <param name="Meta">Flat string attributes (chat_id, message_id, ...).</param>
public sealed record ChannelEvent(string Content, IReadOnlyDictionary<string, string> Meta);

/// <summary>Delivers events to the connected MCP client.</summary>
public interface IChannelNotifier
{
    /// <summary>Waits until an MCP client has initialized the session.</summary>
    Task WaitForClientAsync(CancellationToken ct);

    /// <summary>Sends one event notification.</summary>
    Task NotifyAsync(ChannelEvent channelEvent, CancellationToken ct);
}

/// <summary>Last delivered message id per chat (for catch-up and de-duplication).</summary>
public interface IEventStateStore
{
    /// <summary>Loads the state; empty if none exists.</summary>
    Task<Dictionary<long, long>> LoadAsync(CancellationToken ct);

    /// <summary>Persists the state.</summary>
    Task SaveAsync(IReadOnlyDictionary<long, long> lastDelivered, CancellationToken ct);
}
