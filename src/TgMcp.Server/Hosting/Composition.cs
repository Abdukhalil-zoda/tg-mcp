using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using TgMcp.Application;
using TgMcp.Application.Audit;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;
using TgMcp.Infrastructure.Telegram;

namespace TgMcp.Server.Hosting;

/// <summary>Builds hosts whose only configuration source is TGMCP_ environment variables and whose logs go to stderr.</summary>
public static class Composition
{
    /// <summary>Configuration from TGMCP_* environment variables only ("__" nests sections).</summary>
    public static IConfigurationRoot LoadConfiguration() =>
        new ConfigurationBuilder().AddEnvironmentVariables(PolicyConfig.Prefix).Build();

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
        builder.Services.AddOptions<TelegramOptions>().Bind(builder.Configuration)
            .Validate(o => Path.IsPathRooted(o.DataDir), "TGMCP_DataDir must be an absolute path")
            .ValidateOnStart();

        var config = PolicyConfig.Parse(builder.Configuration.AsEnumerable());
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<IPolicy>(sp => new PolicyEngine(sp.GetRequiredService<PolicyConfig>()));
        return builder;
    }

    /// <summary>Registers the read tools and their pipeline.</summary>
    public static IServiceCollection AddReadTools(this IServiceCollection services)
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
        services.AddHostedService<StartupAudit>();
        return services;
    }
}
