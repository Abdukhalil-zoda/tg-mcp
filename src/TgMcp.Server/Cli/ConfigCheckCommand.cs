using System.Collections;
using Microsoft.Extensions.Configuration;
using TgMcp.Domain;
using TgMcp.Infrastructure.Telegram;
using TgMcp.Server.Hosting;

namespace TgMcp.Server.Cli;

/// <summary><c>tgmcp config check [file]</c>: prints the effective policy per setting and rule; never contacts Telegram.</summary>
public static class ConfigCheckCommand
{
    /// <summary>Runs the check; exit code 0 if valid, 1 if not.</summary>
    public static int Run(IDictionary environment, string? path, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(output);
        var loaded = PolicyConfigLoader.Load(environment, path);
        var state = loaded.FileFound ? "found" : loaded.Explicit ? "NOT FOUND" : "not present (environment only)";
        output.WriteLine($"config file: {loaded.FilePath} - {state}");
        foreach (var line in ConfigReport.Describe(loaded.Policy)) output.WriteLine(line);

        var tg = new TelegramOptions();
        new ConfigurationBuilder().AddEnvironmentVariables(PolicyConfig.Prefix).Build().Bind(tg);
        output.WriteLine($"telegram credentials (env): {(tg.Problem() is string p ? "missing - " + p : "present")}");
        output.WriteLine($"telegram session: {(tg.HasSession() ? "present" : "none (run `tgmcp login`)")} in {tg.DatabaseDir}");
        output.WriteLine(loaded.Policy.IsValid ? "result: OK" : "result: INVALID (the server would deny everything)");
        return loaded.Policy.IsValid ? 0 : 1;
    }
}
