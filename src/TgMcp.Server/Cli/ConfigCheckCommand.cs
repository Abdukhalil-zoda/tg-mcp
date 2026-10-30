using Microsoft.Extensions.Configuration;
using TgMcp.Domain;
using TgMcp.Infrastructure.Telegram;

namespace TgMcp.Server.Cli;

/// <summary><c>tgmcp config check</c>: prints the effective policy per rule; never contacts Telegram.</summary>
public static class ConfigCheckCommand
{
    /// <summary>Runs the check; exit code 0 if valid, 1 if not.</summary>
    public static int Run(IConfiguration configuration, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);
        var config = PolicyConfig.Parse(configuration.AsEnumerable());
        foreach (var line in ConfigReport.Describe(config)) output.WriteLine(line);

        var tg = new TelegramOptions();
        configuration.Bind(tg);
        output.WriteLine($"telegram credentials: {(tg.Problem() is string p ? "missing - " + p : "present")}");
        output.WriteLine($"telegram session: {(tg.HasSession() ? "present" : "none (run `tgmcp login`)")} in {tg.DatabaseDir}");
        output.WriteLine(config.IsValid ? "result: OK" : "result: INVALID (the server would deny everything)");
        return config.IsValid ? 0 : 1;
    }
}
