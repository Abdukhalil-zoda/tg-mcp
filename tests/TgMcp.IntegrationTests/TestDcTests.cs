using TgMcp.Application.Telegram;

namespace TgMcp.IntegrationTests;

/// <summary>Real TDLib against Telegram's test DC: login, send confirmation, history, search, inbound updates.</summary>
public sealed class TestDcTests(TestDcFixture dc) : IClassFixture<TestDcFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TestDcEnv Run()
    {
        Assert.SkipWhen(dc.SkipReason is not null, dc.SkipReason ?? "");
        return new TestDcEnv(dc.Client!, dc.SavedMessagesChatId);
    }

    [Fact]
    public void Login_state_machine_reaches_ready()
    {
        Run();
        Assert.Equal("AuthorizationStateReady", dc.LoginStates[^1]);
        Assert.Contains("AuthorizationStateWaitPhoneNumber", dc.LoginStates);
    }

    [Fact]
    public async Task Send_waits_for_the_final_message_id()
    {
        var g = Run();
        var token = "tgmcp-it " + Guid.NewGuid().ToString("N");
        var sent = await g.Client.SendMessageAsync(new OutgoingMessage(g.Chat, token, 0, null, true), Ct);
        Assert.True(sent.MessageId > 0);
        var back = await g.Client.GetMessageAsync(g.Chat, sent.MessageId, Ct);
        Assert.NotNull(back);
        Assert.Equal(token, back.Text);
        Assert.True(back.FromMe);
    }

    [Fact]
    public async Task History_and_search_find_a_sent_message()
    {
        var g = Run();
        var token = "tgmcp-search-" + Guid.NewGuid().ToString("N")[..12];
        var sent = await g.Client.SendMessageAsync(new OutgoingMessage(g.Chat, token, 0, null, true), Ct);
        var history = await g.Client.GetChatHistoryAsync(g.Chat, 0, 20, Ct);
        Assert.Contains(history, m => m.Id == sent.MessageId);
        IReadOnlyList<TgMessage> found = [];
        for (var i = 0; i < 10 && found.Count == 0; i++)
        {
            found = await g.Client.SearchChatMessagesAsync(g.Chat, token, 0, 10, Ct);
            if (found.Count == 0) await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }
        Assert.Contains(found, m => m.Id == sent.MessageId);
    }

    [Fact]
    public async Task Inbound_stream_carries_the_final_id_of_own_messages()
    {
        var g = Run();
        var token = "tgmcp-event " + Guid.NewGuid().ToString("N");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var reader = Task.Run(async () =>
        {
            await foreach (var m in g.Client.ReadInboundAsync(cts.Token))
                if (m.Text == token) return m;
            return null;
        }, cts.Token);
        var sent = await g.Client.SendMessageAsync(new OutgoingMessage(g.Chat, token, 0, null, true), Ct);
        var seen = await reader;
        Assert.NotNull(seen);
        Assert.Equal(sent.MessageId, seen.Id);
    }

    private sealed record TestDcEnv(TgMcp.Infrastructure.Telegram.TdTelegramClient Client, long Chat);
}
