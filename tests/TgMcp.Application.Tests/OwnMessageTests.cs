using System.Text.Json;
using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;
using static TgMcp.Application.Tests.Harness;

namespace TgMcp.Application.Tests;

public class OwnMessageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TgChat Friend = new(111, "Friend", ["friend"], ChatKind.Private);
    private static readonly TgChat Bank = new(333, "Bank", ["bank_bot"], ChatKind.Bot);

    private static readonly (string, string)[] Global =
        [("Enabled", "true"), ("Features__Send", "true"), ("Features__EditOwn", "true"), ("Features__DeleteOwn", "true")];

    private static (string, string)[] Rule(string write, string features = "edit_own,delete_own") =>
        [("Chats__0__Id", "111"), ("Chats__0__Write", write), ("Chats__0__Features", features)];

    private static Harness H(params (string, string)[] extra)
    {
        var h = new Harness([.. Global, .. extra]);
        Assert.True(h.Policy.Config.IsValid, string.Join("; ", h.Policy.Config.Errors));
        h.Telegram.Add(Friend, Msg(111, 10, "mine", fromMe: true), Msg(111, 11, "also mine, not via TgMcp", fromMe: true), Msg(111, 12, "theirs"));
        h.Telegram.Add(Bank, Msg(333, 20, "x", fromMe: true));
        h.SentLog.Entries.Add((111, 10));
        h.SentLog.Entries.Add((333, 20));
        return h;
    }

    private static JsonElement Json(ToolResult r)
    {
        Assert.False(r.IsError, r.Text);
        return JsonDocument.Parse(r.Text).RootElement;
    }

    // ---------------------------------------------------------------- edit

    [Fact]
    public async Task Edit_on_changes_a_message_sent_through_tgmcp()
    {
        var h = H(Rule("on"));
        Assert.Equal("edited", Json(await h.Edit.RunAsync("111", 10, "fixed typo", Ct)).GetProperty("status").GetString());
        Assert.Equal((111L, 10L, "fixed typo"), Assert.Single(h.Telegram.Edits));
        Assert.Equal("edited message 10, length 10", h.LastAudit.Reason);
    }

    [Theory]
    [InlineData(11)] // own message, but not sent through TgMcp
    [InlineData(12)] // someone else's message
    public async Task Edit_refuses_messages_not_in_the_sent_log(long id)
    {
        var h = H(Rule("on"));
        var r = await h.Edit.RunAsync("111", id, "x", Ct);
        Assert.Contains("was not sent through TgMcp", r.Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Edits);
    }

    [Fact]
    public async Task Edit_draft_then_approve()
    {
        var h = H(Rule("draft"));
        var root = Json(await h.Edit.RunAsync("111", 10, "new text", Ct));
        Assert.Equal("draft", root.GetProperty("status").GetString());
        Assert.Empty(h.Telegram.Edits);
        var id = root.GetProperty("draft_id").GetString()!;
        Assert.Equal(Draft.EditKind, h.DraftStore.Drafts[id].Kind);
        Assert.Equal("edited", Json(await h.DraftCommands.ApproveAsync(id, Ct)).GetProperty("status").GetString());
        Assert.Single(h.Telegram.Edits);
        Assert.DoesNotContain("new text", h.LastAudit.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_needs_the_feature_and_a_write_mode()
    {
        var noFeature = H(Rule("on", "delete_own"));
        Assert.Contains("'edit_own' feature", (await noFeature.Edit.RunAsync("111", 10, "x", Ct)).Text, StringComparison.Ordinal);
        var off = H(Rule("off"));
        Assert.Contains("\"write\": \"off\"", (await off.Edit.RunAsync("111", 10, "x", Ct)).Text, StringComparison.Ordinal);
        var noGlobal = new Harness([("Enabled", "true"), ("Features__Send", "true"), .. Rule("on")]);
        Assert.Contains("\"features.editOwn\" is off", (await noGlobal.Edit.RunAsync("111", 10, "x", Ct)).Text, StringComparison.Ordinal);
        Assert.Equal(0, noGlobal.Telegram.Calls);
    }

    [Fact]
    public async Task Edit_refuses_uneditable_messages_and_bad_text()
    {
        var h = H(Rule("on"));
        h.Telegram.NotEditable.Add((111, 10));
        Assert.Contains("can no longer be edited", (await h.Edit.RunAsync("111", 10, "x", Ct)).Text, StringComparison.Ordinal);
        Assert.StartsWith("bad argument", (await h.Edit.RunAsync("111", 10, new string('a', 4097), Ct)).Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- delete

    [Fact]
    public async Task Delete_is_a_draft_by_default_even_with_write_on()
    {
        var h = H(Rule("on"));
        var root = Json(await h.Delete.RunAsync("111", [10], false, Ct));
        Assert.Equal("draft", root.GetProperty("status").GetString());
        Assert.Empty(h.Telegram.Deletes);
        var id = root.GetProperty("draft_id").GetString()!;
        Assert.Equal("deleted", Json(await h.DraftCommands.ApproveAsync(id, Ct)).GetProperty("status").GetString());
        Assert.Equal((111L, false), (Assert.Single(h.Telegram.Deletes).Chat, h.Telegram.Deletes[0].ForEveryone));
        Assert.DoesNotContain((111L, 10L), h.SentLog.Entries);
    }

    [Fact]
    public async Task Delete_on_needs_write_on_and_delete_mode_on()
    {
        var h = H([.. Rule("on"), ("Delete__Mode", "on")]);
        var root = Json(await h.Delete.RunAsync("111", [10], true, Ct));
        Assert.Equal("deleted", root.GetProperty("status").GetString());
        Assert.True(Assert.Single(h.Telegram.Deletes).ForEveryone);
        Assert.Equal("deleted 1 message(s) for everyone", h.LastAudit.Reason);

        var draftChat = H([.. Rule("draft"), ("Delete__Mode", "on")]);
        Assert.Equal("draft", Json(await draftChat.Delete.RunAsync("111", [10], false, Ct)).GetProperty("status").GetString());
        Assert.Empty(draftChat.Telegram.Deletes);
    }

    [Fact]
    public async Task Delete_refuses_foreign_and_unrevocable_messages()
    {
        var h = H([.. Rule("on"), ("Delete__Mode", "on")]);
        Assert.Contains("was not sent through TgMcp", (await h.Delete.RunAsync("111", [10, 12], false, Ct)).Text, StringComparison.Ordinal);
        h.Telegram.NotRevocable.Add((111, 10));
        Assert.Contains("can't be deleted for everyone", (await h.Delete.RunAsync("111", [10], true, Ct)).Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Deletes);
    }

    [Fact]
    public async Task Delete_needs_the_feature_and_deny_wins()
    {
        var noFeature = H(Rule("on", "edit_own"));
        Assert.Contains("'delete_own' feature", (await noFeature.Delete.RunAsync("111", [10], false, Ct)).Text, StringComparison.Ordinal);
        var deny = H([("Chats__0__Id", "333"), ("Chats__0__Write", "on"), ("Chats__0__Features", "delete_own"), ("Deny__0__Type", "bot")]);
        Assert.Equal(Denials.HardDeny, (await deny.Delete.RunAsync("333", [20], false, Ct)).Text);
    }

    [Fact]
    public async Task Approve_rechecks_before_deleting()
    {
        var h = H(Rule("on"));
        var id = Json(await h.Delete.RunAsync("111", [10], false, Ct)).GetProperty("draft_id").GetString()!;
        h.SentLog.Entries.Clear(); // e.g. the log was reset
        Assert.True((await h.DraftCommands.ApproveAsync(id, Ct)).IsError);
        Assert.Empty(h.Telegram.Deletes);
        Assert.Single(h.DraftStore.Drafts);
    }

    [Fact]
    public void Delete_mode_validation()
    {
        Assert.Equal(WriteMode.Draft, PolicyConfig.FromPairs().DeleteMode);
        Assert.False(PolicyConfig.FromPairs(("Delete__Mode", "off")).IsValid);
        Assert.False(PolicyConfig.FromPairs(("Delete__Bogus", "on")).IsValid);
        Assert.Equal(WriteMode.On, PolicyConfig.FromPairs(("Delete__Mode", "on")).DeleteMode);
    }

    // ---------------------------------------------------------------- sent-log feeding

    [Fact]
    public async Task Sends_approvals_and_forwards_are_recorded_in_the_sent_log()
    {
        var h = new Harness(("Enabled", "true"), ("Features__Send", "true"), ("Features__Read", "true"), ("Features__Forward", "true"),
            ("Chats__0__Id", "111"), ("Chats__0__Write", "on"), ("Chats__0__Read", "true"), ("Chats__0__Features", "forward"),
            ("Chats__1__Id", "222"), ("Chats__1__Write", "draft"));
        h.Telegram.Add(Friend, Msg(111, 1, "a"));
        h.Telegram.Add(new TgChat(222, "Other", [], ChatKind.Private));
        await h.Send.RunAsync("111", "hi", 0, 0, false, Ct);
        Assert.Contains((111L, 1001L), h.SentLog.Entries);
        var draftId = JsonDocument.Parse((await h.Send.RunAsync("222", "later", 0, 0, false, Ct)).Text).RootElement.GetProperty("draft_id").GetString()!;
        await h.DraftCommands.ApproveAsync(draftId, Ct);
        Assert.Contains((222L, 1002L), h.SentLog.Entries);
        await h.Forward.RunAsync("111", [1], "111", 0, false, Ct);
        Assert.Contains((111L, 9001L), h.SentLog.Entries);
    }
}
