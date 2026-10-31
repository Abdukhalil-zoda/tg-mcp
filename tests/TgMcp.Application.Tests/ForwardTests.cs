using System.Text.Json;
using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;
using static TgMcp.Application.Tests.Harness;

namespace TgMcp.Application.Tests;

public class ForwardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TgChat Work = new(-100111, "Work", [], ChatKind.Group);
    private static readonly TgChat Notes = new(222, "Saved", [], ChatKind.Private);
    private static readonly TgChat Forum = new(-100333, "Forum", [], ChatKind.Group, IsForum: true);
    private static readonly TgChat Bank = new(444, "Bank", ["bank_bot"], ChatKind.Bot);

    private static readonly (string, string)[] Global =
        [("Enabled", "true"), ("Features__Read", "true"), ("Features__Forward", "true"), ("Features__Send", "true")];

    private static readonly (string, string)[] Source = [("Chats__0__Id", "-100111"), ("Chats__0__Read", "true"), ("Chats__0__Features", "forward")];

    private static (string, string)[] Dest(string write, string id = "222") => [("Chats__1__Id", id), ("Chats__1__Write", write)];

    private static Harness H(params (string, string)[] extra)
    {
        var h = new Harness([.. Global, .. extra]);
        Assert.True(h.Policy.Config.IsValid, string.Join("; ", h.Policy.Config.Errors));
        h.Telegram.Add(Work, Msg(-100111, 1, "a"), Msg(-100111, 2, "b"), Msg(-100111, 3, "old", daysAgo: 30));
        h.Telegram.Add(Notes);
        h.Telegram.Add(Forum);
        h.Telegram.Add(Bank, Msg(444, 9, "code"));
        return h;
    }

    private static JsonElement Json(ToolResult r)
    {
        Assert.False(r.IsError, r.Text);
        return JsonDocument.Parse(r.Text).RootElement;
    }

    [Fact]
    public async Task On_forwards_into_a_write_only_chat()
    {
        var h = H([.. Source, .. Dest("on")]);
        var root = Json(await h.Forward.RunAsync("-100111", [1, 2], "222", 0, false, Ct));
        Assert.Equal("forwarded", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("message_ids").GetArrayLength());
        var f = Assert.Single(h.Telegram.Forwards);
        Assert.Equal((-100111L, 222L, false), (f.From, f.To, f.Copy));
        Assert.Equal(222, h.LastAudit.ChatId);
        Assert.Equal("forwarded 2 message(s) from -100111", h.LastAudit.Reason);
    }

    [Fact]
    public async Task Draft_then_approve_forwards()
    {
        var h = H([.. Source, .. Dest("draft")]);
        var root = Json(await h.Forward.RunAsync("-100111", [2], "222", 0, true, Ct));
        Assert.Equal("draft", root.GetProperty("status").GetString());
        var id = root.GetProperty("draft_id").GetString()!;
        Assert.Empty(h.Telegram.Forwards);
        Assert.Equal(Draft.ForwardKind, h.DraftStore.Drafts[id].Kind);
        Assert.Equal("forwarded", Json(await h.DraftCommands.ApproveAsync(id, Ct)).GetProperty("status").GetString());
        Assert.True(Assert.Single(h.Telegram.Forwards).Copy);
        Assert.Empty(h.DraftStore.Drafts);
    }

    [Fact]
    public async Task Source_needs_read_and_forward()
    {
        var noForward = H([("Chats__0__Id", "-100111"), ("Chats__0__Read", "true"), .. Dest("on")]);
        Assert.Contains("'forward' feature", (await noForward.Forward.RunAsync("-100111", [1], "222", 0, false, Ct)).Text, StringComparison.Ordinal);
        var noRead = H([("Chats__0__Id", "-100111"), ("Chats__0__Features", "forward"), .. Dest("on")]);
        Assert.Contains("\"read\": false", (await noRead.Forward.RunAsync("-100111", [1], "222", 0, false, Ct)).Text, StringComparison.Ordinal);
        Assert.Empty(noForward.Telegram.Forwards);
    }

    [Fact]
    public async Task Destination_needs_a_write_mode()
    {
        var h = H([.. Source, .. Dest("off")]);
        var r = await h.Forward.RunAsync("-100111", [1], "222", 0, false, Ct);
        Assert.Contains("\"write\": \"off\"", r.Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Forwards);
    }

    [Fact]
    public async Task Deny_wins_on_either_side()
    {
        var toBot = H([.. Source, .. Dest("on", "444"), ("Deny__0__Type", "bot")]);
        Assert.Equal(Denials.HardDeny, (await toBot.Forward.RunAsync("-100111", [1], "444", 0, false, Ct)).Text);
        var fromBot = H([("Chats__0__Id", "444"), ("Chats__0__Read", "true"), ("Chats__0__Features", "forward"), .. Dest("on"), ("Deny__0__Type", "bot")]);
        Assert.Equal(Denials.HardDeny, (await fromBot.Forward.RunAsync("444", [9], "222", 0, false, Ct)).Text);
        Assert.Empty(toBot.Telegram.Forwards);
        Assert.Empty(fromBot.Telegram.Forwards);
    }

    [Fact]
    public async Task Since_days_protected_and_missing_messages_are_refused()
    {
        var h = H([.. Source, ("Chats__0__SinceDays", "7"), .. Dest("on")]);
        Assert.Contains("older than", (await h.Forward.RunAsync("-100111", [1, 3], "222", 0, false, Ct)).Text, StringComparison.Ordinal);
        Assert.Contains("message 77 not found", (await h.Forward.RunAsync("-100111", [77], "222", 0, false, Ct)).Text, StringComparison.Ordinal);
        h.Telegram.Protected.Add((-100111, 2));
        Assert.Contains("can't be forwarded", (await h.Forward.RunAsync("-100111", [2], "222", 0, false, Ct)).Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Forwards);
    }

    [Fact]
    public async Task Destination_topics_are_enforced()
    {
        var h = H([.. Source, ("Chats__1__Id", "-100333"), ("Chats__1__Write", "on"), ("Chats__1__Topics", "5")]);
        Assert.Contains("pass to_topic_id", (await h.Forward.RunAsync("-100111", [1], "-100333", 0, false, Ct)).Text, StringComparison.Ordinal);
        Assert.Contains("Topic 9", (await h.Forward.RunAsync("-100111", [1], "-100333", 9, false, Ct)).Text, StringComparison.Ordinal);
        Assert.False((await h.Forward.RunAsync("-100111", [1], "-100333", 5, false, Ct)).IsError);
        Assert.Equal(5, Assert.Single(h.Telegram.Forwards).Topic);
    }

    [Theory]
    [InlineData("Features__Forward")]
    [InlineData("Features__Send")]
    [InlineData("Features__Read")]
    public async Task Every_global_switch_is_required(string sw)
    {
        var h = new Harness([.. Global.Where(g => g.Item1 != sw), .. Source, .. Dest("on")]);
        Assert.True((await h.Forward.RunAsync("-100111", [1], "222", 0, false, Ct)).IsError);
        Assert.Equal(0, h.Telegram.Calls);
    }

    [Fact]
    public async Task Bad_arguments_and_limits()
    {
        var h = H([.. Source, .. Dest("on"), ("Limits__SendsPerMinute", "1")]);
        Assert.StartsWith("bad argument", (await h.Forward.RunAsync("-100111", [], "222", 0, false, Ct)).Text, StringComparison.Ordinal);
        Assert.StartsWith("bad argument", (await h.Forward.RunAsync("-100111", [.. Enumerable.Range(1, 21).Select(i => (long)i)], "222", 0, false, Ct)).Text, StringComparison.Ordinal);
        Assert.False((await h.Forward.RunAsync("-100111", [1], "222", 0, false, Ct)).IsError);
        Assert.Contains("limits.sendsPerMinute=1", (await h.Forward.RunAsync("-100111", [2], "222", 0, false, Ct)).Text, StringComparison.Ordinal);
    }
}
