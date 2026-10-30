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
      config check    print the effective policy and exit (non-zero if invalid); never contacts Telegram
      help            show this text

    Configuration comes only from TGMCP_* environment variables. See README.md.
    """;

var command = args.Length == 0 ? "serve" : args[0].ToLowerInvariant();
switch (command)
{
    case "serve":
        return await ServeAsync(args[1..]).ConfigureAwait(false);
    case "login":
        return await LoginAsync().ConfigureAwait(false);
    case "config" when args.Length > 1 && args[1] == "check":
        return ConfigCheckCommand.Run(Composition.LoadConfiguration(), Console.Out);
    case "drafts" or "approve" or "reject":
        await Console.Error.WriteLineAsync($"'{command}' is not in this version (planned together with write tools).").ConfigureAwait(false);
        return 2;
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
    builder.Services.AddReadTools();
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
