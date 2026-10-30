using System.Text.Json;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;
using static TgMcp.Application.Tests.Harness;

namespace TgMcp.Application.Tests;

public class SendMessageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TgChat Friend = new(111, "Friend", ["friend"], ChatKind.Private);
    private static readonly TgChat Forum = new(-100222, "Forum", [], ChatKind.Group, IsForum: true);
    private static readonly TgChat Bank = new(333, "MyBank", ["mybank_bot"], ChatKind.Bot);

    private const string Secret = "the secret message text";

    private static Harness H(params (string, string)[] extra)
    {
        var h = new Harness([("Enabled", "true"), ("Features__Send", "true"), .. extra]);
        h.Telegram.Add(Friend, Msg(111, 1, "hi"));
        h.Telegram.Add(Forum, Msg(-100222, 10, "in topic 5", topic: 5), Msg(-100222, 11, "in topic 9", topic: 9));
        h.Telegram.Add(Bank, Msg(333, 20, "code"));
        return h;
    }

    private static JsonElement Json(ToolResult r)
    {
        Assert.False(r.IsError, r.Text);
        return JsonDocument.Parse(r.Text).RootElement;
    }

    [Fact]
    public async Task Write_on_sends_immediately_and_reports_the_final_id()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Write", "on"));
        var root = Json(await h.Send.RunAsync("111", Secret, 0, 0, true, Ct));
        Assert.Equal("sent", root.GetProperty("status").GetString());
        Assert.Equal(1001, root.GetProperty("message_id").GetInt64());
        var sent = Assert.Single(h.Telegram.Sent);
        Assert.Equal(Secret, sent.Text);
        Assert.True(sent.DisableNotification);
        Assert.Equal($"sent, length {Secret.Length}", h.LastAudit.Reason);
    }

    [Fact]
    public async Task Write_draft_saves_and_does_not_send()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Write", "draft"));
        var root = Json(await h.Send.RunAsync("@friend", Secret, 1, 0, false, Ct));
        Assert.Equal("draft", root.GetProperty("status").GetString());
        var id = root.GetProperty("draft_id").GetString()!;
        Assert.Contains($"tgmcp approve {id}", root.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Sent);
        var draft = h.DraftStore.Drafts[id];
        Assert.Equal(Secret, draft.Message.Text);
        Assert.Equal(1, draft.Message.ReplyToMessageId);
        Assert.Equal("Friend", draft.ChatTitle);
    }

    [Fact]
    public async Task Write_off_is_denied_with_a_hint()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Read", "true"));
        var r = await h.Send.RunAsync("111", Secret, 0, 0, false, Ct);
        Assert.True(r.IsError);
        Assert.Contains("rule chats[0] has \"write\": \"off\"", r.Text, StringComparison.Ordinal);
        Assert.Contains("\"write\": \"draft\"", r.Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Sent);

        var none = H();
        var r2 = await none.Send.RunAsync("111", Secret, 0, 0, false, Ct);
        Assert.Contains("add to \"chats\": { \"id\": 111, \"name\": \"Friend\", \"write\": \"draft\" }", r2.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Global_send_switch_is_required()
    {
        var h = new Harness(("Enabled", "true"), ("Chats__0__Id", "111"), ("Chats__0__Write", "on"));
        h.Telegram.Add(Friend);
        var r = await h.Send.RunAsync("111", Secret, 0, 0, false, Ct);
        Assert.True(r.IsError);
        Assert.Contains("\"features.send\" is off", r.Text, StringComparison.Ordinal);
        Assert.Equal(0, h.Telegram.Calls);
    }

    [Fact]
    public async Task Deny_wins_and_reveals_nothing()
    {
        var h = H(("Chats__0__Id", "333"), ("Chats__0__Write", "on"), ("Deny__0__Type", "bot"));
        var r = await h.Send.RunAsync("333", Secret, 0, 0, false, Ct);
        Assert.Equal(Denials.HardDeny, r.Text);
        Assert.Empty(h.Telegram.Sent);
        Assert.Empty(h.DraftStore.Drafts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_text_is_rejected(string text)
    {
        var h = H(("Defaults__Write", "on"));
        var r = await h.Send.RunAsync("111", text, 0, 0, false, Ct);
        Assert.True(r.IsError);
        Assert.Contains("text is empty", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_over_4096_characters_is_rejected()
    {
        var h = H(("Defaults__Write", "on"));
        Assert.False((await h.Send.RunAsync("111", new string('a', 4096), 0, 0, false, Ct)).IsError);
        var r = await h.Send.RunAsync("111", new string('a', 4097), 0, 0, false, Ct);
        Assert.True(r.IsError);
        Assert.Contains("4097", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Topics_restrict_where_messages_may_go()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Write", "on"), ("Chats__0__Topics", "5"));
        Assert.Contains("only be written to in topics [5]; pass topic_id", (await h.Send.RunAsync("-100222", Secret, 0, 0, false, Ct)).Text, StringComparison.Ordinal);
        var wrong = await h.Send.RunAsync("-100222", Secret, 0, 9, false, Ct);
        Assert.Contains("Topic 9", wrong.Text, StringComparison.Ordinal);
        var badReply = await h.Send.RunAsync("-100222", Secret, 11, 5, false, Ct);  // reply target in topic 9
        Assert.True(badReply.IsError);
        Assert.Empty(h.Telegram.Sent);
        Assert.False((await h.Send.RunAsync("-100222", Secret, 10, 5, false, Ct)).IsError);
        Assert.Equal(5, Assert.Single(h.Telegram.Sent).TopicId);
    }

    [Fact]
    public async Task Per_chat_send_limit_applies()
    {
        var h = H(("Defaults__Write", "on"), ("Limits__SendsPerMinute", "2"));
        Assert.False((await h.Send.RunAsync("111", "a", 0, 0, false, Ct)).IsError);
        Assert.False((await h.Send.RunAsync("111", "b", 0, 0, false, Ct)).IsError);
        var r = await h.Send.RunAsync("111", "c", 0, 0, false, Ct);
        Assert.True(r.IsError);
        Assert.Contains("limits.sendsPerMinute=2", r.Text, StringComparison.Ordinal);
        Assert.False((await h.Send.RunAsync("-100222", "d", 0, 0, false, Ct)).IsError); // other chat unaffected
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.False((await h.Send.RunAsync("111", "e", 0, 0, false, Ct)).IsError);
    }

    [Fact]
    public async Task Send_failure_is_reported()
    {
        var h = H(("Defaults__Write", "on"));
        h.Telegram.SendFailure = new TelegramCallException(403, "CHAT_WRITE_FORBIDDEN");
        var r = await h.Send.RunAsync("111", Secret, 0, 0, false, Ct);
        Assert.True(r.IsError);
        Assert.Contains("CHAT_WRITE_FORBIDDEN", r.Text, StringComparison.Ordinal);
        Assert.Equal("telegram error 403", h.LastAudit.Reason);
    }

    [Fact]
    public async Task Text_never_reaches_the_audit_log()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Write", "on"), ("Chats__1__Id", "-100222"), ("Chats__1__Write", "draft"));
        await h.Send.RunAsync("111", Secret, 0, 0, false, Ct);
        await h.Send.RunAsync("-100222", Secret, 0, 0, false, Ct);
        await h.Send.RunAsync("333", Secret, 0, 0, false, Ct);
        Assert.Equal(3, h.Audit.Entries.Count);
        Assert.All(h.Audit.Entries, e => Assert.DoesNotContain("secret", $"{e.Tool} {e.Rule} {e.Reason}", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------- drafts: approve / reject

    [Fact]
    public async Task Approve_sends_the_draft_and_deletes_it()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Write", "draft"));
        var id = Json(await h.Send.RunAsync("111", Secret, 0, 0, false, Ct)).GetProperty("draft_id").GetString()!;
        var r = await h.DraftCommands.ApproveAsync(id, Ct);
        Assert.Equal("sent", Json(r).GetProperty("status").GetString());
        Assert.Equal(Secret, Assert.Single(h.Telegram.Sent).Text);
        Assert.Empty(h.DraftStore.Drafts);
        Assert.Equal("approve", h.LastAudit.Tool);
        Assert.DoesNotContain("secret", h.LastAudit.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approve_rechecks_the_policy()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Write", "draft"));
        var id = Json(await h.Send.RunAsync("111", Secret, 0, 0, false, Ct)).GetProperty("draft_id").GetString()!;
        var denied = new Harness(("Enabled", "true"), ("Features__Send", "true"), ("Chats__0__Id", "111"), ("Chats__0__Write", "draft"), ("Deny__0__Id", "111"));
        denied.Telegram.Add(Friend);
        await denied.DraftStore.SaveAsync("Friend", h.DraftStore.Drafts[id].Message, Ct);
        var r = await denied.DraftCommands.ApproveAsync("00000001", Ct);
        Assert.Equal(Denials.HardDeny, r.Text);
        Assert.Empty(denied.Telegram.Sent);
        Assert.Single(denied.DraftStore.Drafts); // kept
    }

    [Fact]
    public async Task Approve_unknown_draft_fails()
    {
        var h = H(("Defaults__Write", "draft"));
        Assert.True((await h.DraftCommands.ApproveAsync("deadbeef", Ct)).IsError);
    }

    [Fact]
    public async Task Reject_deletes_without_sending()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Write", "draft"));
        var id = Json(await h.Send.RunAsync("111", Secret, 0, 0, false, Ct)).GetProperty("draft_id").GetString()!;
        Assert.True(await h.DraftCommands.RejectAsync(id, Ct));
        Assert.False(await h.DraftCommands.RejectAsync(id, Ct));
        Assert.Empty(h.Telegram.Sent);
        Assert.Equal("reject", h.LastAudit.Tool);
        Assert.Empty(await h.DraftCommands.ListAsync(Ct));
    }
}
