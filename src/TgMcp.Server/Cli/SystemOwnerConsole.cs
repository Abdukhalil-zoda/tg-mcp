using System.Text;
using TgMcp.Infrastructure.Telegram;

namespace TgMcp.Server.Cli;

/// <summary>Owner console on the process terminal. Secrets are read without echo.</summary>
public sealed class SystemOwnerConsole : IOwnerConsole
{
    /// <inheritdoc />
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <inheritdoc />
    public void Say(string text) => Console.Out.WriteLine(text);

    /// <inheritdoc />
    public string Ask(string prompt)
    {
        Console.Out.Write(prompt);
        return Console.In.ReadLine() ?? "";
    }

    /// <inheritdoc />
    public string AskSecret(string prompt)
    {
        Console.Out.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.Out.WriteLine();
        return sb.ToString();
    }
}
