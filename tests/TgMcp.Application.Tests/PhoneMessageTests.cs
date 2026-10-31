using System.Text.Json;
using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;

namespace TgMcp.Application.Tests;

public class PhoneMessageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Number = "+15551234567";
    private static readonly TgChat Person = new(777, "New Person", [], ChatKind.Private);

    private static Harness H(params (string, string)[] extra)
    {
        var h = new Harness([("Enabled", "true"), ("Features__Send", "true"), ("ContactsAllow", Number), .. extra]);
        Assert.True(h.Policy.Config.IsValid, string.Join("; ", h.Policy.Config.Errors));
        h.Telegram.PhoneUsers[Number] = Person;
        return h;
    }

    private static Task<ToolResult> Send(Harness h, string phone, string text = "Hello from the owner") =>
        h.Send.RunAsync(null, text, 0, 0, false, phone, "Alice Example", Ct);

    private static JsonElement Json(ToolResult r)
    {
        Assert.False(r.IsError, r.Text);
        return JsonDocument.Parse(r.Text).RootElement;
    }

    [Fact]
    public async Task Disabled_by_default_with_a_hint()
    {
        var h = H();
        var r = await Send(h, Number);
        Assert.Contains("\"contacts\": { \"mode\": \"draft\" }", r.Text, StringComparison.Ordinal);
        Assert.Contains("TGMCP_ContactsAllow", r.Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Imports);
        Assert.Empty(h.Telegram.Sent);
    }

    [Fact]
    public async Task Draft_saves_without_importing_and_approve_imports_one_contact_and_sends()
    {
        var h = H(("Contacts__Mode", "draft"));
        var root = Json(await Send(h, "+1 (555) 123-4567"));
        Assert.Equal("draft", root.GetProperty("status").GetString());
        Assert.Empty(h.Telegram.Imports);
        Assert.Empty(h.Telegram.Sent);
        var id = root.GetProperty("draft_id").GetString()!;
        Assert.Equal(Draft.PhoneKind, h.DraftStore.Drafts[id].Kind);
        Assert.DoesNotContain("5551234567", h.LastAudit.Reason, StringComparison.Ordinal);
        Assert.Contains("phone #", h.LastAudit.Reason, StringComparison.Ordinal);

        var sent = Json(await h.DraftCommands.ApproveAsync(id, Ct));
        Assert.Equal("sent", sent.GetProperty("status").GetString());
        Assert.True(sent.GetProperty("contact_imported").GetBoolean());
        Assert.Equal((Number, "Alice Example"), Assert.Single(h.Telegram.Imports));
        Assert.Equal(777, Assert.Single(h.Telegram.Sent).ChatId);
        Assert.Empty(h.DraftStore.Drafts);
        Assert.Contains((777L, 1001L), h.SentLog.Entries);
        Assert.DoesNotContain("5551234567", h.LastAudit.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_sends_directly_and_returns_the_rule_to_add()
    {
        var h = H(("Contacts__Mode", "on"));
        var root = Json(await Send(h, Number));
        Assert.Equal("sent", root.GetProperty("status").GetString());
        Assert.Equal("{ \"id\": 777, \"name\": \"New Person\", \"read\": true }", root.GetProperty("rule_to_add").GetString());
        Assert.Single(h.Telegram.Sent);
    }

    [Fact]
    public async Task Known_contacts_are_not_imported_again()
    {
        var h = H(("Contacts__Mode", "on"));
        h.Telegram.LocalContacts.Add(Number);
        Assert.False(Json(await Send(h, Number)).GetProperty("contact_imported").GetBoolean());
        Assert.Empty(h.Telegram.Imports);
    }

    [Fact]
    public async Task Numbers_outside_the_allow_list_are_refused_in_every_mode()
    {
        foreach (var mode in new[] { "draft", "on" })
        {
            var h = H(("Contacts__Mode", mode));
            var r = await Send(h, "+15559999999");
            Assert.Contains("not in TGMCP_ContactsAllow", r.Text, StringComparison.Ordinal);
            Assert.Empty(h.DraftStore.Drafts);
            Assert.Empty(h.Telegram.Imports);
        }
        var none = new Harness(("Enabled", "true"), ("Features__Send", "true"), ("Contacts__Mode", "on"));
        Assert.Contains("No phone numbers are allowed", (await Send(none, Number)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_chat_limit_per_day()
    {
        var h = H(("Contacts__Mode", "draft"), ("Limits__NewChatsPerDay", "1"));
        Assert.False((await Send(h, Number)).IsError);
        Assert.Contains("limits.newChatsPerDay=1", (await Send(h, Number)).Text, StringComparison.Ordinal);
        h.Time.Advance(TimeSpan.FromHours(25));
        Assert.False((await Send(h, Number)).IsError);
    }

    [Fact]
    public async Task Deny_rules_and_explicit_write_off_on_the_resolved_chat_win()
    {
        var deny = H(("Contacts__Mode", "on"), ("Deny__0__Id", "777"));
        Assert.Equal(Denials.HardDeny, (await Send(deny, Number)).Text);
        var off = H(("Contacts__Mode", "on"), ("Chats__0__Id", "777"), ("Chats__0__Write", "off"));
        Assert.Contains("\"write\": \"off\"", (await Send(off, Number)).Text, StringComparison.Ordinal);
        Assert.Empty(deny.Telegram.Sent);
        Assert.Empty(off.Telegram.Sent);
    }

    [Fact]
    public async Task Unknown_numbers_and_bad_arguments()
    {
        var h = new Harness(("Enabled", "true"), ("Features__Send", "true"), ("Contacts__Mode", "on"), ("ContactsAllow", "+15551234567,+447700900123"));
        h.Telegram.PhoneUsers[Number] = Person;
        Assert.Contains("not on Telegram", (await Send(h, "+447700900123")).Text, StringComparison.Ordinal);
        Assert.StartsWith("bad argument", (await Send(h, "5551234567")).Text, StringComparison.Ordinal);
        var both = await h.Send.RunAsync("111", "x", 0, 0, false, Number, null, Ct);
        Assert.StartsWith("bad argument", both.Text, StringComparison.Ordinal);
        var neither = await h.Send.RunAsync(null, "x", 0, 0, false, null, null, Ct);
        Assert.StartsWith("bad argument", neither.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_rechecks_the_allow_list()
    {
        var h = H(("Contacts__Mode", "draft"));
        var id = Json(await Send(h, Number)).GetProperty("draft_id").GetString()!;
        var removed = new Harness(("Enabled", "true"), ("Features__Send", "true"), ("Contacts__Mode", "draft"), ("ContactsAllow", "+447700900123"));
        await removed.DraftStore.SavePhoneAsync(h.DraftStore.Drafts[id].Phone!, Ct);
        Assert.True((await removed.DraftCommands.ApproveAsync("00000001", Ct)).IsError);
        Assert.Empty(removed.Telegram.Imports);
        Assert.Single(removed.DraftStore.Drafts);
    }
}
