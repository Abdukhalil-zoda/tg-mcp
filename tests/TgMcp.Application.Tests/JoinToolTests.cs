using System.Text.Json;
using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;

namespace TgMcp.Application.Tests;

public class JoinToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TgChatPreview Channel = new(-1001234567890, "Example Channel", "channel", ChatKind.Channel, ["example_channel"], 1200, false, false);
    private static readonly TgChatPreview Private = new(null, "Private Group", "supergroup", ChatKind.Group, [], 40, true, false);

    private static Harness H(params (string, string)[] extra)
    {
        var h = new Harness([("Enabled", "true"), ("Features__Join", "true"), .. extra]);
        Assert.True(h.Policy.Config.IsValid, string.Join("; ", h.Policy.Config.Errors));
        h.Telegram.Previews["example_channel"] = Channel;
        h.Telegram.Previews["AbCdEfGh12"] = Private;
        return h;
    }

    private static JsonElement Json(ToolResult r)
    {
        Assert.False(r.IsError, r.Text);
        return JsonDocument.Parse(r.Text).RootElement;
    }

    [Fact]
    public async Task Off_is_denied_with_the_snippet_to_enable_it()
    {
        var h = H();
        var r = await h.Join.RunAsync("@example_channel", Ct);
        Assert.True(r.IsError);
        Assert.Contains("\"features\": { \"join\": true } and \"join\": { \"mode\": \"draft\" }", r.Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Joins);
        Assert.Equal(0, h.Telegram.Calls);
    }

    [Fact]
    public async Task Features_join_off_blocks_before_telegram()
    {
        var h = new Harness(("Enabled", "true"), ("Join__Mode", "on"), ("Join__Allow", "*"));
        var r = await h.Join.RunAsync("@example_channel", Ct);
        Assert.Contains("\"features.join\" is off", r.Text, StringComparison.Ordinal);
        Assert.Equal(0, h.Telegram.Calls);
        Assert.True((await h.Preview.RunAsync("@example_channel", Ct)).IsError);
    }

    [Fact]
    public async Task On_joins_allow_listed_targets_and_returns_the_rule_to_add()
    {
        var h = H(("Join__Mode", "on"), ("Join__Allow", "@example_channel"));
        var root = Json(await h.Join.RunAsync("https://t.me/example_channel", Ct));
        Assert.Equal("joined", root.GetProperty("status").GetString());
        Assert.Equal(-1001234567890, root.GetProperty("chat_id").GetInt64());
        Assert.Equal("{ \"id\": -1001234567890, \"name\": \"Example Channel\", \"read\": true }", root.GetProperty("rule_to_add").GetString());
        Assert.Single(h.Telegram.Joins);
        Assert.Equal("username @example_channel, mode on, joined", h.LastAudit.Reason);
        Assert.Equal(-1001234567890, h.LastAudit.ChatId);
    }

    [Fact]
    public async Task On_refuses_targets_outside_the_allow_list_and_empty_lists()
    {
        var h = H(("Join__Mode", "on"), ("Join__Allow", "@other_channel"));
        var r = await h.Join.RunAsync("@example_channel", Ct);
        Assert.Contains("not in \"join.allow\"", r.Text, StringComparison.Ordinal);
        var empty = H(("Join__Mode", "on"));
        Assert.Contains("\"join.allow\" is empty", (await empty.Join.RunAsync("@example_channel", Ct)).Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Joins);
        Assert.Empty(empty.Telegram.Joins);
    }

    [Fact]
    public async Task Joining_grants_no_read_access()
    {
        var h = H(("Join__Mode", "on"), ("Join__Allow", "*"), ("Features__Read", "true"));
        await h.Join.RunAsync("@example_channel", Ct);
        h.Telegram.Add(new TgChat(-1001234567890, "Example Channel", ["example_channel"], ChatKind.Channel));
        var read = await h.Read.RunAsync("-1001234567890", 10, 0, Ct);
        Assert.True(read.IsError);
        Assert.Contains("is not allowed", read.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deny_rules_win_on_the_resolved_chat()
    {
        var h = H(("Join__Mode", "on"), ("Join__Allow", "*"), ("Deny__0__Type", "channel"));
        var r = await h.Join.RunAsync("@example_channel", Ct);
        Assert.Equal(Denials.HardDeny, r.Text);
        Assert.Empty(h.Telegram.Joins);
        Assert.Equal(Denials.HardDeny, (await h.Preview.RunAsync("@example_channel", Ct)).Text);
    }

    [Fact]
    public async Task Join_rate_limit_is_per_hour()
    {
        var h = H(("Join__Mode", "draft"), ("Limits__JoinsPerHour", "2"));
        Assert.False((await h.Join.RunAsync("@example_channel", Ct)).IsError);
        Assert.False((await h.Join.RunAsync("@example_channel", Ct)).IsError);
        var r = await h.Join.RunAsync("@example_channel", Ct);
        Assert.Contains("limits.joinsPerHour=2", r.Text, StringComparison.Ordinal);
        h.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.True((await h.Join.RunAsync("@example_channel", Ct)).IsError);
        h.Time.Advance(TimeSpan.FromMinutes(25));
        Assert.False((await h.Join.RunAsync("@example_channel", Ct)).IsError);
    }

    [Fact]
    public async Task Draft_then_approve_joins_and_reject_deletes()
    {
        var h = H(("Join__Mode", "draft"));
        var root = Json(await h.Join.RunAsync("https://t.me/+AbCdEfGh12?utm=x", Ct));
        Assert.Equal("draft", root.GetProperty("status").GetString());
        var id = root.GetProperty("draft_id").GetString()!;
        Assert.Contains($"tgmcp approve {id}", root.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True(root.GetProperty("join_request_needed").GetBoolean());
        Assert.Empty(h.Telegram.Joins);
        var draft = h.DraftStore.Drafts[id];
        Assert.Equal(Draft.JoinKind, draft.Kind);
        Assert.Equal("AbCdEfGh12", draft.Join!.TargetValue);
        Assert.DoesNotContain("AbCdEfGh12", h.LastAudit.Reason, StringComparison.Ordinal);
        Assert.Contains("invite invite#", h.LastAudit.Reason, StringComparison.Ordinal);

        h.Telegram.JoinStatus = "requested";
        var approved = Json(await h.DraftCommands.ApproveAsync(id, Ct));
        Assert.Equal("requested", approved.GetProperty("status").GetString());
        Assert.Equal("AbCdEfGh12", Assert.Single(h.Telegram.Joins).Value);
        Assert.Empty(h.DraftStore.Drafts);
        Assert.Equal("approve", h.LastAudit.Tool);
        Assert.DoesNotContain("AbCdEfGh12", h.LastAudit.Reason, StringComparison.Ordinal);

        var second = Json(await h.Join.RunAsync("@example_channel", Ct)).GetProperty("draft_id").GetString()!;
        Assert.True(await h.DraftCommands.RejectAsync(second, Ct));
        Assert.Single(h.Telegram.Joins);
    }

    [Fact]
    public async Task Approve_rechecks_join_mode()
    {
        var h = H(("Join__Mode", "draft"));
        var id = Json(await h.Join.RunAsync("@example_channel", Ct)).GetProperty("draft_id").GetString()!;
        var off = new Harness(("Enabled", "true"), ("Features__Join", "true"));
        off.Telegram.Previews["example_channel"] = Channel;
        await off.DraftStore.SaveJoinAsync("Example Channel", h.DraftStore.Drafts[id].Join!, Ct);
        var r = await off.DraftCommands.ApproveAsync("00000001", Ct);
        Assert.True(r.IsError);
        Assert.Contains("Joining chats is off", r.Text, StringComparison.Ordinal);
        Assert.Empty(off.Telegram.Joins);
        Assert.Single(off.DraftStore.Drafts);
    }

    [Fact]
    public async Task Bad_targets_are_rejected()
    {
        var h = H(("Join__Mode", "on"), ("Join__Allow", "*"));
        foreach (var bad in new[] { "example_channel", "tg://join?invite=AbCdEfGh12", "https://t.me/+15551234567", "https://t.me/c/123/4" })
        {
            var r = await h.Join.RunAsync(bad, Ct);
            Assert.True(r.IsError, bad);
            Assert.StartsWith("bad argument:", r.Text, StringComparison.Ordinal);
        }
        Assert.Empty(h.Telegram.Joins);
    }

    [Fact]
    public async Task Preview_does_not_join()
    {
        var h = H();   // join.mode off: preview still works with features.join
        var root = Json(await h.Preview.RunAsync("@example_channel", Ct));
        Assert.Equal("Example Channel", root.GetProperty("title").GetString());
        Assert.Equal("channel", root.GetProperty("type").GetString());
        Assert.Equal(1200, root.GetProperty("member_count").GetInt32());
        Assert.False(root.GetProperty("join_request_needed").GetBoolean());
        Assert.Equal("off", root.GetProperty("join_mode").GetString());
        Assert.Empty(h.Telegram.Joins);
    }

    [Fact]
    public async Task Already_member_does_not_join_again()
    {
        var h = H(("Join__Mode", "on"), ("Join__Allow", "*"));
        h.Telegram.Previews["example_channel"] = Channel with { AlreadyMember = true };
        Assert.Equal("already_member", Json(await h.Join.RunAsync("@example_channel", Ct)).GetProperty("status").GetString());
        Assert.Empty(h.Telegram.Joins);
    }
}
