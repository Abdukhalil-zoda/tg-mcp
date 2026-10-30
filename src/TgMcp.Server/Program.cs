using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TgMcp.Infrastructure.Telegram;
using TgMcp.Server.Cli;
using TgMcp.Server.Hosting;
using TgMcp.Server.Mcp;

const string Usage = """
    usage: tgmcp [command]

      serve           run the MCP server on stdio (default)
      login           log in to Telegram interactively (owner only, needs a TTY)
      config check [file]
                      print the effective policy and exit (non-zero if invalid); never contacts Telegram
      drafts          list drafts waiting for approval
      approve <id>    send a draft as the owner, then delete it (stop the MCP server first: one TDLib process per data dir)
      reject <id>     delete a draft without sending it
      help            show this text

    The policy comes from the JSON file at TGMCP_ConfigFile (default /config/tgmcp.json),
    overridden by TGMCP_* environment variables. Secrets are environment-only. See README.md.
    """;

var command = args.Length == 0 ? "serve" : args[0].ToLowerInvariant();
switch (command)
{
    case "serve":
        return await ServeAsync(args[1..]).ConfigureAwait(false);
    case "login":
        return await LoginAsync().ConfigureAwait(false);
    case "config" when args.Length > 1 && args[1] == "check":
        return ConfigCheckCommand.Run(Environment.GetEnvironmentVariables(), args.Length > 2 ? args[2] : null, Console.Out);
    case "drafts" or "approve" or "reject":
        return await DraftCommandsCli.RunAsync(command, args.Length > 1 ? args[1] : null, Console.Out, CancellationToken.None).ConfigureAwait(false);
    case "help" or "--help" or "-h":
        Console.Out.WriteLine(Usage);
        return 0;
    default:
        await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
        return 2;
}

static async Task<int> ServeAsync(string[] rest)
{
    // stdout belongs to the MCP transport; anything else that writes to Console.Out goes to stderr.
    var stdout = Console.OpenStandardOutput();
    Console.SetOut(Console.Error);

    var builder = Composition.CreateBuilder(rest);
    builder.Services.AddTelegramTools();
    builder.Services
        .AddMcpServer(o => o.ServerInfo = new() { Name = "tgmcp", Version = typeof(Composition).Assembly.GetName().Version?.ToString(3) ?? "0.1.0" })
        .WithStreamServerTransport(Console.OpenStandardInput(), stdout)
        .WithTools<TelegramTools>();
    using var host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
    return 0;
}

static async Task<int> LoginAsync()
{
    var builder = Composition.CreateBuilder([]);
    builder.Services.AddSingleton<IOwnerConsole, SystemOwnerConsole>();
    builder.Services.AddSingleton<LoginFlow>();
    using var host = builder.Build();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    return await host.Services.GetRequiredService<LoginFlow>().RunAsync(cts.Token).ConfigureAwait(false);
}
