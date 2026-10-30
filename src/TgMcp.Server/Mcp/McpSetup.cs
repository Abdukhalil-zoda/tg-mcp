using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TgMcp.Domain;
using TgMcp.Server.Events;

namespace TgMcp.Server.Mcp;

/// <summary>Server info, instructions and capabilities for the MCP session.</summary>
public static class McpSetup
{
    /// <summary>Applies TgMcp's settings; declares <c>experimental["claude/channel"]</c> only when events are enabled.</summary>
    public static void Configure(McpServerOptions options, IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        var config = (PolicyConfig?)services.FirstOrDefault(d => d.ServiceType == typeof(PolicyConfig))?.ImplementationInstance;
        Configure(options, config is { IsValid: true, EventsEnabled: true });
    }

    /// <summary>Applies TgMcp's settings.</summary>
    public static void Configure(McpServerOptions options, bool channelEvents)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ServerInfo = new Implementation
        {
            Name = "tgmcp",
            Version = typeof(McpSetup).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
        };
        options.ServerInstructions = ServerInstructions.Text;
        if (channelEvents)
        {
            options.Capabilities ??= new ServerCapabilities();
            options.Capabilities.Experimental ??= new Dictionary<string, object>();
            // A JsonElement, because the SDK serializes capability values with its source-generated context.
            using var empty = System.Text.Json.JsonDocument.Parse("{}");
            options.Capabilities.Experimental[McpChannelNotifier.Capability] = empty.RootElement.Clone();
        }
    }
}
