using System.Text.Json;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;
using static TgMcp.Application.Tests.Harness;

namespace TgMcp.Application.Tests;

public class ReadNewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const long GroupId = -1001234567890;
    private static readonly TgChat Group = new(GroupId, "Example Group", [], ChatKind.Group);
    private static readonly TgChat Channel = new(-1009876543210, "Example Channel", ["example_channel"], ChatKind.Channel);
    private static readonly TgChat Other = new(-1005555555555, "Other", [], ChatKind.Group);

    private static readonly (string, string)[] Base = [("Enabled", "true"), ("Features__Read", "true")];

    private static Harness H(params (string, string)[] extra)
    {
        var h = new Harness([.. Base, .. extra]);
        h.Telegram.Add(Group, [.. Enumerable.Range(1, 10).Select(i => Msg(GroupId, i, $"m{i}"))]);
        return h;
    }

    private static readonly (string, string)[] ReadAll = [("Defaults__Read", "true")];

    private static ReadNewChat[] One(string chat, long afterId) => [new() { Chat = chat, AfterId = afterId }];

    private static JsonElement Root(ToolResult r)
    {
        Assert.False(r.IsError, r.Text);
        return JsonDocument.Parse(r.Text).RootElement;
    }

    private static JsonElement Chat(ToolResult r, int i = 0) => Root(r).GetProperty("chats")[i];

    private static long[] Ids(JsonElement chat) =>
        [.. chat.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetInt64())];

    [Fact]
    public async Task Returns_only_messages_after_after_id_oldest_first()
    {
        var h = H(ReadAll);
        var r = await h.ReadNew.RunAsync(One(GroupId.ToString(System.Globalization.CultureInfo.InvariantCulture), 6), 50, 1500, false, Ct);
        var c = Chat(r);
        Assert.Equal(new long[] { 7, 8, 9, 10 }, Ids(c));
        Assert.Equal(10, c.GetProperty("last_id").GetInt64());
        Assert.False(c.GetProperty("more").GetBoolean());
        Assert.False(c.TryGetProperty("gap", out _));
        Assert.Equal(GroupId, c.GetProperty("chat_id").GetInt64());
        Assert.Equal("Example Group", c.GetProperty("title").GetString());
        Assert.Equal(ToolResult.ThirdPartyNote, Root(r).GetProperty("note").GetString());
        var m = c.GetProperty("messages")[0];
        Assert.Equal("m7", m.GetProperty("text").GetString());
        Assert.Equal("2030-01-15T12:00:00Z", m.GetProperty("date").GetString());
        Assert.Equal("Alice", m.GetProperty("sender_name").GetString());
        Assert.False(m.TryGetProperty("truncated", out _));
        Assert.False(m.TryGetProperty("kind", out _));
    }

    [Fact]
    public async Task Nothing_new_keeps_last_id()
    {
        var h = H(ReadAll);
        var c = Chat(await h.ReadNew.RunAsync(One("-1001234567890", 10), 50, 1500, false, Ct));
        Assert.Empty(Ids(c));
        Assert.Equal(10, c.GetProperty("last_id").GetInt64());
        Assert.False(c.GetProperty("more").GetBoolean());
    }

    [Fact]
    public async Task Limit_sets_more_and_the_next_window_continues_from_last_id()
    {
        var h = H(ReadAll);
        var first = Chat(await h.ReadNew.RunAsync(One("-1001234567890", 2), 3, 1500, false, Ct));
        Assert.Equal(new long[] { 3, 4, 5 }, Ids(first));
        Assert.True(first.GetProperty("more").GetBoolean());
        Assert.Equal(5, first.GetProperty("last_id").GetInt64());

        var second = Chat(await h.ReadNew.RunAsync(One("-1001234567890", 5), 3, 1500, false, Ct));
        Assert.Equal(new long[] { 6, 7, 8 }, Ids(second));
        var third = Chat(await h.ReadNew.RunAsync(One("-1001234567890", 8), 3, 1500, false, Ct));
        Assert.Equal(new long[] { 9, 10 }, Ids(third));
        Assert.False(third.GetProperty("more").GetBoolean());
        Assert.Equal(10, third.GetProperty("last_id").GetInt64());
    }

    [Fact]
    public async Task Bootstrap_returns_only_the_newest_page()
    {
        var h = H(ReadAll);
        var c = Chat(await h.ReadNew.RunAsync(One("-1001234567890", 0), 3, 1500, false, Ct));
        Assert.Equal(new long[] { 8, 9, 10 }, Ids(c));
        Assert.False(c.GetProperty("more").GetBoolean());
        Assert.Equal(10, c.GetProperty("last_id").GetInt64());
    }

    [Fact]
    public async Task Denied_and_failing_chats_do_not_fail_the_others()
    {
        var h = H(("Chats__0__Id", "-1001234567890"), ("Chats__0__Read", "true"));
        h.Telegram.Add(Other, Msg(Other.Id, 1, "secret"));
        ReadNewChat[] chats =
        [
            new() { Chat = "-1005555555555", AfterId = 0 },
            new() { Chat = "@no_such_chat", AfterId = 0 },
            new() { Chat = "-1001234567890", AfterId = 8 },
        ];
        var r = await h.ReadNew.RunAsync(chats, 50, 1500, false, Ct);
        var denied = Chat(r, 0);
        Assert.Contains("is not allowed", denied.GetProperty("denied").GetString(), StringComparison.Ordinal);
        Assert.False(denied.TryGetProperty("messages", out _));
        Assert.DoesNotContain("secret", r.Text, StringComparison.Ordinal);
        Assert.Contains("no public chat", Chat(r, 1).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(new long[] { 9, 10 }, Ids(Chat(r, 2)));
        Assert.Equal(2, Root(r).GetProperty("total").GetInt32());
        Assert.Single(h.Audit.Entries);
        Assert.Equal("read_new", h.LastAudit.Tool);
        Assert.Equal(2, h.LastAudit.Count);
        Assert.Equal("ok, 3 chats, 1 denied, 1 failed", h.LastAudit.Reason);
    }

    [Fact]
    public async Task Deny_rules_stay_opaque()
    {
        var h = H([.. ReadAll, ("Deny__0__Id", "-1005555555555")]);
        h.Telegram.Add(Other, Msg(Other.Id, 1, "secret"));
        var c = Chat(await h.ReadNew.RunAsync(One("-1005555555555", 0), 50, 1500, false, Ct));
        Assert.Equal(Denials.HardDeny, c.GetProperty("denied").GetString());
        Assert.False(c.TryGetProperty("chat_id", out _));
    }

    [Fact]
    public async Task Without_the_read_switch_the_whole_call_is_denied()
    {
        var h = new Harness(("Enabled", "true"), ("Defaults__Read", "true"));
        h.Telegram.Add(Group, Msg(GroupId, 1, "x"));
        var r = await h.ReadNew.RunAsync(One("-1001234567890", 0), 50, 1500, false, Ct);
        Assert.True(r.IsError);
        Assert.Equal(0, h.Telegram.Calls);
    }

    [Fact]
    public async Task Long_texts_are_truncated_and_marked()
    {
        var h = H(ReadAll);
        h.Telegram.Add(Channel, Msg(Channel.Id, 1, "0123456789abcdef"), Msg(Channel.Id, 2, "short"), Msg(Channel.Id, 3, "abcd😀xyz"));
        var c = Chat(await h.ReadNew.RunAsync(One("@example_channel", 0), 50, 5, false, Ct));
        var msgs = c.GetProperty("messages");
        Assert.Equal("01234", msgs[0].GetProperty("text").GetString());
        Assert.True(msgs[0].GetProperty("truncated").GetBoolean());
        Assert.Equal("short", msgs[1].GetProperty("text").GetString());
        Assert.False(msgs[1].TryGetProperty("truncated", out _));
        Assert.Equal("abcd", msgs[2].GetProperty("text").GetString()); // never splits a surrogate pair
    }

    [Fact]
    public async Task Inputs_are_clamped_and_validated()
    {
        var h = H(ReadAll);
        h.Telegram.Add(Channel, [.. Enumerable.Range(1, 150).Select(i => Msg(Channel.Id, i, "text " + i))]);
        Assert.Single(Ids(Chat(await h.ReadNew.RunAsync(One("@example_channel", 0), 0, 1500, false, Ct))));
        Assert.Equal(100, Ids(Chat(await h.ReadNew.RunAsync(One("@example_channel", 0), 1000, 1500, false, Ct))).Length);
        Assert.Equal("t", Chat(await h.ReadNew.RunAsync(One("@example_channel", 0), 1, 0, false, Ct)).GetProperty("messages")[0].GetProperty("text").GetString());

        var empty = await h.ReadNew.RunAsync([], 50, 1500, false, Ct);
        Assert.True(empty.IsError);
        Assert.Contains("1-50", empty.Text, StringComparison.Ordinal);
        var tooMany = await h.ReadNew.RunAsync([.. Enumerable.Range(0, 51).Select(_ => new ReadNewChat { Chat = "@example_channel" })], 50, 1500, false, Ct);
        Assert.True(tooMany.IsError);
        Assert.Contains("after_id", Chat(await h.ReadNew.RunAsync(One("@example_channel", -1), 50, 1500, false, Ct)).GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Policy_max_messages_per_call_caps_each_chat()
    {
        var h = H([.. ReadAll, ("Limits__MaxMessagesPerCall", "4")]);
        Assert.Equal(new long[] { 7, 8, 9, 10 }, Ids(Chat(await h.ReadNew.RunAsync(One("-1001234567890", 0), 50, 1500, false, Ct))));
    }

    [Fact]
    public async Task Topic_rules_filter_and_last_id_moves_past_dropped_messages()
    {
        var h = new Harness([.. Base, ("Chats__0__Id", "-1001234567890"), ("Chats__0__Read", "true"), ("Chats__0__Topics", "5")]);
        h.Telegram.Add(Group, Msg(GroupId, 1, "a", topic: 5), Msg(GroupId, 2, "b", topic: 9), Msg(GroupId, 3, "c", topic: 5), Msg(GroupId, 4, "d", topic: 9));
        var c = Chat(await h.ReadNew.RunAsync(One("-1001234567890", 0), 50, 1500, false, Ct));
        Assert.Equal(new long[] { 1, 3 }, Ids(c));
        Assert.Equal(4, c.GetProperty("last_id").GetInt64());
    }

    [Fact]
    public async Task Since_days_stops_the_scan()
    {
        var h = new Harness([.. Base, ("Chats__0__Id", "-1001234567890"), ("Chats__0__Read", "true"), ("Chats__0__SinceDays", "3")]);
        h.Telegram.Add(Group, Msg(GroupId, 1, "old", 10), Msg(GroupId, 2, "new", 1));
        Assert.Equal(new long[] { 2 }, Ids(Chat(await h.ReadNew.RunAsync(One("-1001234567890", 0), 50, 1500, false, Ct))));
        Assert.Equal(new long[] { 2 }, Ids(Chat(await h.ReadNew.RunAsync(One("-1001234567890", 1), 50, 1500, false, Ct))));
    }

    [Fact]
    public async Task Service_messages_have_empty_text_and_a_kind()
    {
        var h = H(ReadAll);
        h.Telegram.Add(Channel, Msg(Channel.Id, 1, "[chataddmembers]") with { Kind = "chataddmembers" },
            Msg(Channel.Id, 2, "[photo] for sale") with { Kind = "photo", Media = new TgMedia(5, 100, "photo", null, "image/jpeg") },
            Msg(Channel.Id, 3, "plain") with { Kind = "text", ReplyToMessageId = 2, SenderId = 42 });
        var msgs = Chat(await h.ReadNew.RunAsync(One("@example_channel", 0), 50, 1500, false, Ct)).GetProperty("messages");
        Assert.Equal("", msgs[0].GetProperty("text").GetString());
        Assert.Equal("chataddmembers", msgs[0].GetProperty("kind").GetString());
        Assert.Equal("[photo] for sale", msgs[1].GetProperty("text").GetString());
        Assert.Equal("photo", msgs[1].GetProperty("kind").GetString());
        Assert.False(msgs[1].TryGetProperty("media", out _));
        Assert.False(msgs[2].TryGetProperty("kind", out _));
        Assert.Equal(2, msgs[2].GetProperty("reply_to").GetInt64());
        Assert.Equal(42, msgs[2].GetProperty("sender_id").GetInt64());
    }

    [Fact]
    public void Links_use_the_username_or_the_internal_id()
    {
        const long server = 1L << 20;
        Assert.Equal("https://t.me/example_channel/7", ReadNewTool.Link(Channel, "example_channel", 7 * server));
        Assert.Equal("https://t.me/c/1234567890/7", ReadNewTool.Link(Group, null, 7 * server));
        Assert.Null(ReadNewTool.Link(new TgChat(-4001234, "Basic", [], ChatKind.Group), null, 7 * server));
        Assert.Null(ReadNewTool.Link(new TgChat(111, "Friend", ["friend"], ChatKind.Private), "friend", 7 * server));
        Assert.Null(ReadNewTool.Link(Group, null, 7)); // local / not yet sent
    }

    [Fact]
    public async Task Gap_is_reported_when_after_id_is_beyond_the_scan_budget()
    {
        var h = H(ReadAll);
        var total = ReadNewTool.ScanPages * MessageCollector.PageSize + 50;
        h.Telegram.Add(Channel, [.. Enumerable.Range(1, total).Select(i => Msg(Channel.Id, i, "x"))]);
        var calls = h.Telegram.Calls;
        var c = Chat(await h.ReadNew.RunAsync(One("@example_channel", 1), 10, 1500, false, Ct));
        Assert.True(c.GetProperty("gap").GetBoolean());
        Assert.True(c.GetProperty("more").GetBoolean());
        Assert.Equal(10, Ids(c).Length);
        Assert.True(h.Telegram.Calls - calls <= ReadNewTool.ScanPages + 1); // resolve + bounded history pages
    }

    [Fact]
    public async Task Export_writes_the_full_result_and_returns_a_summary_without_text()
    {
        ReadNewChat[] chats = [new() { Chat = "-1001234567890", AfterId = 7 }, new() { Chat = "-1005555555555" }];
        var h2 = H(("Chats__0__Id", "-1001234567890"), ("Chats__0__Read", "true"));
        h2.Telegram.Add(Other, Msg(Other.Id, 1, "hidden"));
        var r = await h2.ReadNew.RunAsync(chats, 50, 1500, true, Ct);
        var root = Root(r);
        Assert.Equal("/data/exports/read_new-1.json", root.GetProperty("export_path").GetString());
        Assert.Equal("read_new-1.json", root.GetProperty("file_name").GetString());
        Assert.Equal(3, root.GetProperty("total").GetInt32());
        var s = root.GetProperty("chats");
        Assert.Equal(3, s[0].GetProperty("count").GetInt32());
        Assert.Equal(10, s[0].GetProperty("last_id").GetInt64());
        Assert.False(s[0].GetProperty("more").GetBoolean());
        Assert.True(s[1].TryGetProperty("denied", out _));
        Assert.False(s[0].TryGetProperty("messages", out _));
        Assert.DoesNotContain("m8", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", r.Text, StringComparison.Ordinal);

        var (prefix, json) = Assert.Single(h2.Exports.Written);
        Assert.Equal("read_new", prefix);
        var full = JsonDocument.Parse(json).RootElement;
        Assert.Equal(new long[] { 8, 9, 10 }, Ids(full.GetProperty("chats")[0]));
        Assert.Contains("m8", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", json, StringComparison.Ordinal);
    }
}
