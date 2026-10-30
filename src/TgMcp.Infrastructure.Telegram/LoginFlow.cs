using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgMcp.Application.Telegram;
using static TdLib.TdApi;
using static TdLib.TdApi.AuthorizationState;

namespace TgMcp.Infrastructure.Telegram;

/// <summary>Interactive terminal used by the login flow. Answers are never logged.</summary>
public interface IOwnerConsole
{
    /// <summary>True if both stdin and stdout are an interactive terminal.</summary>
    bool IsInteractive { get; }

    /// <summary>Shows a line to the owner.</summary>
    void Say(string text);

    /// <summary>Asks for a visible answer.</summary>
    string Ask(string prompt);

    /// <summary>Asks for a secret without echo.</summary>
    string AskSecret(string prompt);
}

/// <summary>
/// Owner-only login: phone number, code and 2FA password are read from the terminal and passed straight
/// to TDLib. The session appears as "TgMcp" in Telegram, Settings, Devices.
/// </summary>
public sealed class LoginFlow
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(5);

    private readonly TelegramOptions _o;
    private readonly IOwnerConsole _console;
    private readonly TimeProvider _time;
    private readonly ILogger<LoginFlow> _log;

    /// <summary>Creates the flow.</summary>
    public LoginFlow(IOptions<TelegramOptions> options, IOwnerConsole console, TimeProvider time, ILogger<LoginFlow> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        _o = options.Value;
        _console = console;
        _time = time;
        _log = log;
    }

    /// <summary>Runs the login; returns a process exit code.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!_console.IsInteractive)
        {
            _console.Say("login needs an interactive terminal (docker run -it ...). Aborting.");
            return 2;
        }
        if (_o.Problem() is string problem)
        {
            _console.Say($"cannot log in: {problem}");
            return 2;
        }

        var session = new TdSession(_o, _time, _log);
        await using (session.ConfigureAwait(false))
        {
            var state = await session.WaitSettledAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (session.ParametersError is string paramsError)
            {
                _console.Say($"cannot log in: {TdTelegramClient.DatabaseProblem(paramsError)}");
                return 2;
            }
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    switch (state)
                    {
                        case AuthorizationStateReady:
                            var me = await session.CallAsync(new GetMe(), ct).ConfigureAwait(false);
                            _console.Say($"Logged in as {me.FirstName} {me.LastName} (user id {me.Id}). Session: TgMcp.");
                            return 0;
                        case AuthorizationStateWaitPhoneNumber:
                            await session.CallAsync(new SetAuthenticationPhoneNumber
                            {
                                PhoneNumber = _console.Ask("Phone number (international format, e.g. +15551234567): ").Trim(),
                            }, ct).ConfigureAwait(false);
                            break;
                        case AuthorizationStateWaitCode:
                            await session.CallAsync(new CheckAuthenticationCode
                            {
                                Code = _console.Ask("Login code (sent to your Telegram app): ").Trim(),
                            }, ct).ConfigureAwait(false);
                            break;
                        case AuthorizationStateWaitPassword p:
                            var hint = string.IsNullOrEmpty(p.PasswordHint) ? "" : $" (hint: {p.PasswordHint})";
                            await session.CallAsync(new CheckAuthenticationPassword
                            {
                                Password = _console.AskSecret($"Two-step verification password{hint}: "),
                            }, ct).ConfigureAwait(false);
                            break;
                        case null:
                            _console.Say("TDLib did not respond in time.");
                            return 1;
                        default:
                            _console.Say($"Unsupported login step: {state.GetType().Name}. Log in to this account with an official app first.");
                            return 1;
                    }
                }
                catch (TelegramCallException e)
                {
                    _console.Say($"Telegram rejected this step: {e.TdMessage}. Try again.");
                    continue;
                }
                state = await session.WaitChangeAsync(state, StepTimeout, ct).ConfigureAwait(false);
            }
            return 1;
        }
    }
}
