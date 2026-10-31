using System.Text.Json;
using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;
using static TgMcp.Application.Tests.Harness;

namespace TgMcp.Application.Tests;

public class ReactTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TgChat Friend = new(111, "Friend", ["friend"], ChatKind.Private);
    private static readonly TgChat Bank = new(333, "MyBank", ["mybank_bot"], ChatKind.Bot);

    private static readonly (string, string)[] Global =
        [("Enabled", "true"), ("Features__Read", "true"), ("Features__React", "true"), ("Features__Send", "true")];

    private static (string, string)[] Rule(string write, string features = "react") =>
        [("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Features", features), ("Chats__0__Write", write)];

    private static Harness H(params (string, string)[] extra)
    {
        var h = new Harness([.. Global, .. extra]);
        Assert.True(h.Policy.Config.IsValid, string.Join("; ", h.Policy.Config.Errors));
        h.Telegram.Add(Friend, Msg(111, 5, "nice"), Msg(111, 1, "old", daysAgo: 40));
        h.Telegram.Add(Bank, Msg(333, 7, "code"));
        return h;
    }

    private static JsonElement Json(ToolResult r)
    {
        Assert.False(r.IsError, r.Text);
        return JsonDocument.Parse(r.Text).RootElement;
    }

    [Fact]
    public async Task On_adds_an_available_reaction()
    {
        var h = H(Rule("on"));
        var root = Json(await h.React.RunAsync("111", 5, "👍", Ct));
        Assert.Equal("added", root.GetProperty("status").GetString());
        Assert.Equal((111L, 5L, "👍", true), Assert.Single(h.Telegram.Reactions));
        Assert.Equal("reaction 👍 added on message 5", h.LastAudit.Reason);
    }

    [Fact]
    public async Task Null_or_empty_emoji_removes_the_owners_reactions()
    {
        var h = H(Rule("on"));
        h.Telegram.ReactionOptions = h.Telegram.ReactionOptions with { Chosen = ["❤", "🔥"] };
        var root = Json(await h.React.RunAsync("111", 5, null, Ct));
        Assert.Equal("removed", root.GetProperty("status").GetString());
        Assert.Equal(2, h.Telegram.Reactions.Count(r => !r.Added));

        var none = H(Rule("on"));
        Assert.Contains("no reaction on this message", (await none.React.RunAsync("111", 5, " ", Ct)).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("🤩", "needs Telegram Premium")]
    [InlineData("🦄", "is not an available reaction")]
    public async Task Unavailable_reactions_are_rejected(string emoji, string error)
    {
        var h = H(Rule("on"));
        var r = await h.React.RunAsync("111", 5, emoji, Ct);
        Assert.True(r.IsError);
        Assert.Contains(error, r.Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Reactions);
    }

    [Fact]
    public async Task Draft_then_approve_applies_the_reaction()
    {
        var h = H(Rule("draft"));
        var root = Json(await h.React.RunAsync("111", 5, "🔥", Ct));
        Assert.Equal("draft", root.GetProperty("status").GetString());
        var id = root.GetProperty("draft_id").GetString()!;
        Assert.Empty(h.Telegram.Reactions);
        Assert.Equal(Draft.ReactionKind, h.DraftStore.Drafts[id].Kind);

        var approved = Json(await h.DraftCommands.ApproveAsync(id, Ct));
        Assert.Equal("added", approved.GetProperty("status").GetString());
        Assert.Equal((111L, 5L, "🔥", true), Assert.Single(h.Telegram.Reactions));
        Assert.Empty(h.DraftStore.Drafts);
        Assert.Equal("approve", h.LastAudit.Tool);
    }

    [Fact]
    public async Task Draft_validates_before_saving()
    {
        var h = H(Rule("draft"));
        Assert.True((await h.React.RunAsync("111", 5, "🦄", Ct)).IsError);
        Assert.Empty(h.DraftStore.Drafts);
    }

    [Fact]
    public async Task Approve_rechecks_the_policy()
    {
        var h = H(Rule("draft"));
        var id = Json(await h.React.RunAsync("111", 5, "👍", Ct)).GetProperty("draft_id").GetString()!;
        var off = H(Rule("off"));
        await off.DraftStore.SaveReactionAsync("Friend", h.DraftStore.Drafts[id].Reaction!, Ct);
        var r = await off.DraftCommands.ApproveAsync("00000001", Ct);
        Assert.True(r.IsError);
        Assert.Contains("\"write\": \"off\"", r.Text, StringComparison.Ordinal);
        Assert.Empty(off.Telegram.Reactions);
        Assert.Single(off.DraftStore.Drafts);
    }

    [Fact]
    public async Task Write_off_is_denied_with_a_hint()
    {
        var h = H(Rule("off"));
        var r = await h.React.RunAsync("111", 5, "👍", Ct);
        Assert.Contains("\"write\": \"draft\"", r.Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Reactions);
    }

    [Fact]
    public async Task Missing_react_feature_is_denied_with_a_hint()
    {
        var h = H(Rule("on", "search"));
        var r = await h.React.RunAsync("111", 5, "👍", Ct);
        Assert.Contains("set \"features\": [\"react\", \"search\"] in rule chats[0]", r.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Features__React")]
    [InlineData("Features__Send")]
    [InlineData("Features__Read")]
    public async Task Every_global_switch_is_required(string sw)
    {
        var h = new Harness([.. Global.Where(g => g.Item1 != sw), .. Rule("on")]);
        h.Telegram.Add(Friend, Msg(111, 5, "nice"));
        var r = await h.React.RunAsync("111", 5, "👍", Ct);
        Assert.True(r.IsError);
        Assert.Equal(0, h.Telegram.Calls);
    }

    [Fact]
    public async Task Deny_wins_and_since_days_apply()
    {
        var deny = H([("Chats__0__Id", "333"), ("Chats__0__Read", "true"), ("Chats__0__Features", "react"), ("Chats__0__Write", "on"), ("Deny__0__Type", "bot")]);
        Assert.Equal(Denials.HardDeny, (await deny.React.RunAsync("333", 7, "👍", Ct)).Text);

        var old = H([.. Rule("on"), ("Chats__0__SinceDays", "7")]);
        Assert.Contains("older than", (await old.React.RunAsync("111", 1, "👍", Ct)).Text, StringComparison.Ordinal);
        Assert.Empty(old.Telegram.Reactions);
    }

    [Fact]
    public async Task Per_chat_send_limit_applies()
    {
        var h = H([.. Rule("on"), ("Limits__SendsPerMinute", "1")]);
        Assert.False((await h.React.RunAsync("111", 5, "👍", Ct)).IsError);
        Assert.Contains("limits.sendsPerMinute=1", (await h.React.RunAsync("111", 5, "❤", Ct)).Text, StringComparison.Ordinal);
    }
}
