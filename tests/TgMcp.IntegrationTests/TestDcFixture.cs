using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TdLib;
using TgMcp.Infrastructure.Telegram;
using static TdLib.TdApi;
using static TdLib.TdApi.AuthorizationState;

namespace TgMcp.IntegrationTests;

/// <summary>
/// Logs a throwaway account in to Telegram's TEST data centers once per run. Opt-in via environment:
/// TGMCP_IT_ApiId, TGMCP_IT_ApiHash, TGMCP_IT_Phone (test number 99966XYYYY) and optionally TGMCP_IT_Code
/// (default: the DC digit X repeated five times). Without them, every test is skipped.
/// </summary>
public sealed class TestDcFixture : IAsyncLifetime
{
    /// <summary>Why the tests are skipped, or null when they run.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>The logged-in client (null when skipped).</summary>
    public TdTelegramClient? Client { get; private set; }

    /// <summary>The account's Saved Messages chat id.</summary>
    public long SavedMessagesChatId { get; private set; }

    /// <summary>Authorization states seen during login, in order.</summary>
    public List<string> LoginStates { get; } = [];

    private string? _dataDir;

    public async ValueTask InitializeAsync()
    {
        var apiId = Environment.GetEnvironmentVariable("TGMCP_IT_ApiId");
        var apiHash = Environment.GetEnvironmentVariable("TGMCP_IT_ApiHash");
        var phone = Environment.GetEnvironmentVariable("TGMCP_IT_Phone");
        if (string.IsNullOrEmpty(apiId) || string.IsNullOrEmpty(apiHash) || string.IsNullOrEmpty(phone))
        {
            SkipReason = "integration tests are opt-in: set TGMCP_IT_ApiId, TGMCP_IT_ApiHash and TGMCP_IT_Phone (test DC number 99966XYYYY)";
            return;
        }
        if (!phone.StartsWith("99966", StringComparison.Ordinal) || phone.Length != 10)
        {
            SkipReason = "TGMCP_IT_Phone must be a test DC number 99966XYYYY; real numbers are never used";
            return;
        }
        var code = Environment.GetEnvironmentVariable("TGMCP_IT_Code") is { Length: > 0 } c ? c : new string(phone[5], 5);

        _dataDir = Path.Combine(Path.GetTempPath(), "tgmcp-it-" + Guid.NewGuid().ToString("N"));
        var options = new TelegramOptions
        {
            ApiId = apiId,
            ApiHash = apiHash,
            DatabaseKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            DataDir = _dataDir,
            UseTestDc = true,
        };

        var session = new TdSession(options, TimeProvider.System, NullLogger.Instance);
        await using (session.ConfigureAwait(false))
        {
            var state = await session.WaitSettledAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
            for (var step = 0; step < 10 && state is not AuthorizationStateReady; step++)
            {
                LoginStates.Add(state?.GetType().Name ?? "none");
                switch (state)
                {
                    case AuthorizationStateWaitPhoneNumber:
                        await session.CallAsync(new SetAuthenticationPhoneNumber { PhoneNumber = phone }, CancellationToken.None);
                        break;
                    case AuthorizationStateWaitCode:
                        await session.CallAsync(new CheckAuthenticationCode { Code = code }, CancellationToken.None);
                        break;
                    case AuthorizationStateWaitRegistration:
                        await session.CallAsync(new RegisterUser { FirstName = "TgMcp", LastName = "IT" }, CancellationToken.None);
                        break;
                    default:
                        throw new InvalidOperationException($"unsupported login step on the test DC: {state?.GetType().Name ?? "timeout"}");
                }
                state = await session.WaitChangeAsync(state, TimeSpan.FromSeconds(60), CancellationToken.None);
            }
            if (state is not AuthorizationStateReady) throw new InvalidOperationException("test DC login did not complete");
            LoginStates.Add(nameof(AuthorizationStateReady));
            var me = await session.CallAsync(new GetMe(), CancellationToken.None);
            SavedMessagesChatId = (await session.CallAsync(new CreatePrivateChat { UserId = me.Id, Force = true }, CancellationToken.None)).Id;
        }

        // The login session is closed; the adapter opens the same database like the server does.
        Client = new TdTelegramClient(Options.Create(options), TimeProvider.System, NullLogger<TdTelegramClient>.Instance);
        await Client.EnsureReadyAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (Client is not null) await Client.DisposeAsync();
        if (_dataDir is not null)
        {
            try
            {
                Directory.Delete(_dataDir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
