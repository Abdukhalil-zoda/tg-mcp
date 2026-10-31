using System.Text.Json;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;
using static TgMcp.Application.Tests.Harness;

namespace TgMcp.Application.Tests;

public class MarkReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TgChat Friend = new(111, "Friend", ["friend"], ChatKind.Private);
    private static readonly TgChat Forum = new(-100222, "Forum", [], ChatKind.Group, IsForum: true);

    private static Harness H(params (string, string)[] extra)
    {
        var h = new Harness([("Enabled", "true"), ("Features__Read", "true"), ("Features__MarkRead", "true"), .. extra]);
        Assert.True(h.Policy.Config.IsValid, string.Join("; ", h.Policy.Config.Errors));
        h.Telegram.Add(Friend, Msg(111, 1, "a"), Msg(111, 2, "b"));
        h.Telegram.Add(Forum, Msg(-100222, 5, "x", topic: 3));
        return h;
    }

    private static readonly (string, string)[] Allowed = [("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Features", "mark_read")];

    [Fact]
    public async Task Marks_everything_read_by_default()
    {
        var h = H(Allowed);
        var root = JsonDocument.Parse((await h.MarkRead.RunAsync("111", 0, Ct)).Text).RootElement;
        Assert.Equal("read", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("up_to_message_id").GetInt64());
        Assert.True(root.GetProperty("mentions_and_reactions_cleared").GetBoolean());
        Assert.Equal((111L, (long?)null), Assert.Single(h.Telegram.MarkedRead));
        Assert.Equal("marked read up to 2, mentions and reactions cleared", h.LastAudit.Reason);
    }

    [Fact]
    public async Task Marks_up_to_a_message()
    {
        var h = H(Allowed);
        var root = JsonDocument.Parse((await h.MarkRead.RunAsync("@friend", 1, Ct)).Text).RootElement;
        Assert.Equal(1, root.GetProperty("up_to_message_id").GetInt64());
        Assert.False(root.GetProperty("mentions_and_reactions_cleared").GetBoolean());
        Assert.True((await h.MarkRead.RunAsync("111", 99, Ct)).IsError); // unknown message
    }

    [Fact]
    public async Task Needs_the_mark_read_feature_with_a_hint()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Read", "true"));
        var r = await h.MarkRead.RunAsync("111", 0, Ct);
        Assert.True(r.IsError);
        Assert.Contains("set \"features\": [\"mark_read\"] in rule chats[0]", r.Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.MarkedRead);
    }

    [Fact]
    public async Task Needs_read()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Features", "mark_read"));
        Assert.Contains("\"read\": false", (await h.MarkRead.RunAsync("111", 0, Ct)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Global_switch_off_blocks_before_telegram()
    {
        var h = new Harness([("Enabled", "true"), ("Features__Read", "true"), .. Allowed]);
        var r = await h.MarkRead.RunAsync("111", 0, Ct);
        Assert.Contains("\"features.markRead\" is off", r.Text, StringComparison.Ordinal);
        Assert.Equal(0, h.Telegram.Calls);
    }

    [Fact]
    public async Task Deny_wins()
    {
        var h = H([.. Allowed, ("Deny__0__Id", "111")]);
        Assert.Equal(Denials.HardDeny, (await h.MarkRead.RunAsync("111", 0, Ct)).Text);
        Assert.Empty(h.Telegram.MarkedRead);
    }

    [Fact]
    public async Task Topic_restricted_rules_are_refused()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Features", "mark_read"), ("Chats__0__Topics", "3"));
        var r = await h.MarkRead.RunAsync("-100222", 0, Ct);
        Assert.Contains("topic-restricted", r.Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.MarkedRead);
    }

    [Fact]
    public async Task Global_rate_limit_applies()
    {
        var h = H([.. Allowed, ("Limits__RatePerMinute", "1")]);
        Assert.False((await h.MarkRead.RunAsync("111", 0, Ct)).IsError);
        Assert.Contains("rate limit", (await h.MarkRead.RunAsync("111", 0, Ct)).Text, StringComparison.Ordinal);
    }
}
