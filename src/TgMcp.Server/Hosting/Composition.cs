using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using TgMcp.Application;
using TgMcp.Application.Audit;
using TgMcp.Application.Drafts;
using TgMcp.Application.Events;
using TgMcp.Server.Events;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;
using TgMcp.Infrastructure.Telegram;

namespace TgMcp.Server.Hosting;

/// <summary>Builds hosts configured from the JSON policy file and TGMCP_ environment variables, with logs on stderr.</summary>
public static class Composition
{
    /// <summary>A host builder with config, stderr logging, options and the policy registered.</summary>
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });
        builder.Configuration.AddEnvironmentVariables(PolicyConfig.Prefix);

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
        });
        // stdout is the MCP channel: every log level goes to stderr.
        builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddOptions<ServerOptions>().Bind(builder.Configuration)
            .Validate(o => Path.IsPathRooted(o.DataDir) && Path.IsPathRooted(o.EffectiveAuditPath), "TGMCP_DataDir and TGMCP_AuditPath must be absolute paths")
            .ValidateOnStart();
        var loaded = PolicyConfigLoader.Load(Environment.GetEnvironmentVariables());
        var config = loaded.Policy;
        builder.Services.AddSingleton(loaded);
        builder.Services.AddSingleton(config);
        builder.Services.AddOptions<TelegramOptions>().Bind(builder.Configuration)
            .PostConfigure(o =>
            {
                // media.dir / media.hostDir come from the validated policy (file or env).
                o.MediaDir = config.MediaDir;
                o.MediaHostDir = config.MediaHostDir;
            })
            .Validate(o => Path.IsPathRooted(o.DataDir), "TGMCP_DataDir must be an absolute path")
            .ValidateOnStart();
        builder.Services.AddSingleton<IPolicy>(sp => new PolicyEngine(sp.GetRequiredService<PolicyConfig>()));
        return builder;
    }

    /// <summary>Registers the tools, drafts and their pipeline.</summary>
    public static IServiceCollection AddTelegramTools(this IServiceCollection services)
    {
        services.AddSingleton<IAuditSink, JsonlAuditSink>();
        services.AddSingleton<IRateLimiter>(sp => new TokenBucket(sp.GetRequiredService<IPolicy>().Config.RatePerMinute, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<TdTelegramClient>();
        services.AddSingleton<ITelegramClient>(sp => sp.GetRequiredService<TdTelegramClient>());
        services.AddSingleton<IMediaStore, FileMediaStore>();
        services.AddSingleton<FolderGuard>();
        services.AddSingleton<ToolRunner>();
        services.AddSingleton<ChatResolver>();
        services.AddSingleton<ToolContext>();
        services.AddSingleton<ListChatsTool>();
        services.AddSingleton<ReadMessagesTool>();
        services.AddSingleton<ReadTopicTool>();
        services.AddSingleton<GetMessageTool>();
        services.AddSingleton<SearchChatTool>();
        services.AddSingleton<SearchAllTool>();
        services.AddSingleton<DownloadMediaTool>();
        services.AddSingleton<ISendLimiter>(sp => new PerChatRateLimiter(sp.GetRequiredService<IPolicy>().Config.SendsPerMinute, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IDraftStore, FileDraftStore>();
        services.AddSingleton<INewChatLimiter>(sp => new NewChatRateLimiter(sp.GetRequiredService<IPolicy>().Config.NewChatsPerDay, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<SendMessageTool>();
        services.AddSingleton<IJoinLimiter>(sp => new JoinRateLimiter(sp.GetRequiredService<IPolicy>().Config.JoinsPerHour, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<JoinChatTool>();
        services.AddSingleton<PreviewChatTool>();
        services.AddSingleton<MarkReadTool>();
        services.AddSingleton<ReactTool>();
        services.AddSingleton<ForwardTool>();
        services.AddSingleton<ISentLog, FileSentLog>();
        services.AddSingleton<EditMessageTool>();
        services.AddSingleton<AiTools>();
        services.AddSingleton<DeleteMessageTool>();
        services.AddSingleton<DraftCommands>();
        services.AddSingleton<IInboundMessageSource>(sp => sp.GetRequiredService<TdTelegramClient>());
        services.AddSingleton<IEventStateStore, FileEventStateStore>();
        services.AddSingleton<IChannelNotifier, McpChannelNotifier>();
        services.AddSingleton<InboundEventProcessor>();
        services.AddHostedService<StartupAudit>();
        services.AddHostedService<MediaCleanupOnStart>();
        return services;
    }
}
