using TgMcp.Application.Events;
using TgMcp.Application.Telegram;
using TgMcp.Domain;
using static TgMcp.Application.Tests.Harness;

namespace TgMcp.Application.Tests;

public class EventTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TgChat Friend = new(111, "Friend", ["friend"], ChatKind.Private);
    private static readonly TgChat Group = new(-100222, "Dev Team", [], ChatKind.Group, IsForum: true);
    private static readonly TgChat Bot = new(333, "Helper", ["helper_bot"], ChatKind.Bot);
    private static readonly TgChat Channel = new(-100444, "News", ["news"], ChatKind.Channel);

    private static readonly (string, string)[] On =
        [("Enabled", "true"), ("Features__Read", "true"), ("Events__Enabled", "true")];

    private static Harness H(params (string, string)[] extra)
    {
        var h = new Harness([.. On, .. extra]);
        Assert.True(h.Policy.Config.IsValid, string.Join("; ", h.Policy.Config.Errors));
        foreach (var c in new[] { Friend, Group, Bot, Channel }) h.Telegram.Add(c);
        return h;
    }

    private static TgMessage In(long chat, long id, string text = "hello", double minutesAgo = 0, bool fromMe = false,
        int? topic = null, bool senderIsBot = false, TgMedia? media = null, long? replyTo = null) =>
        new(chat, id, Now.AddMinutes(-minutesAgo), "Alice", fromMe, text, media, topic, 42, "alice", senderIsBot, replyTo);

    private static readonly (string, string)[] FriendNotify = [("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Notify", "true")];

    [Fact]
    public async Task Emits_for_a_notify_chat()
    {
        var h = H(FriendNotify);
        Assert.Equal(EventOutcome.Emitted, await h.Events.HandleAsync(In(111, 5), false, Ct));
        var ev = Assert.Single(h.Notifier.Events);
        Assert.StartsWith(EventFormatter.Marker + "\n", ev.Content, StringComparison.Ordinal);
        Assert.EndsWith("hello", ev.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_disabled_emit_nothing()
    {
        var h = new Harness(("Enabled", "true"), ("Features__Read", "true"), ("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Notify", "true"));
        h.Telegram.Add(Friend);
        Assert.Equal(EventOutcome.Skipped, await h.Events.HandleAsync(In(111, 5), false, Ct));
        Assert.Empty(h.Notifier.Events);
    }

    public static TheoryData<string, (string, string)[], long, bool, bool, bool, EventOutcome> Matrix => new()
    {
        { "no notify", [("Chats__0__Id", "111"), ("Chats__0__Read", "true")], 111, false, false, false, EventOutcome.Skipped },
        { "unmatched chat, defaults off", FriendNotify, -100222, false, false, false, EventOutcome.Skipped },
        { "outgoing", FriendNotify, 111, true, false, false, EventOutcome.Skipped },
        { "outgoing included", [.. FriendNotify, ("Events__IncludeOutgoing", "true")], 111, true, false, false, EventOutcome.Emitted },
        { "deny wins", [.. FriendNotify, ("Deny__0__Id", "111")], 111, false, false, false, EventOutcome.Skipped },
        { "defaults notify, person", [("Defaults__Read", "true"), ("Defaults__Notify", "true")], 111, false, false, false, EventOutcome.Emitted },
        { "defaults notify, bot chat", [("Defaults__Read", "true"), ("Defaults__Notify", "true")], 333, false, false, false, EventOutcome.Skipped },
        { "defaults notify, channel", [("Defaults__Read", "true"), ("Defaults__Notify", "true")], -100444, false, false, false, EventOutcome.Skipped },
        { "defaults notify, bot sender in group", [("Defaults__Read", "true"), ("Defaults__Notify", "true")], -100222, false, true, false, EventOutcome.Skipped },
        { "explicit rule, bot chat", [("Chats__0__Id", "333"), ("Chats__0__Read", "true"), ("Chats__0__Notify", "true")], 333, false, false, false, EventOutcome.Emitted },
        { "explicit rule, channel", [("Chats__0__Username", "news"), ("Chats__0__Read", "true"), ("Chats__0__Notify", "true")], -100444, false, false, false, EventOutcome.Emitted },
        { "explicit rule, bot sender", [("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Notify", "true")], -100222, false, true, false, EventOutcome.Emitted },
        { "topic outside rule topics", [("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Notify", "true"), ("Chats__0__Topics", "5")], -100222, false, false, true, EventOutcome.Skipped },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Gating_matrix(string name, (string, string)[] rules, long chat, bool fromMe, bool senderIsBot, bool topic9, EventOutcome expected)
    {
        var h = H(rules);
        var outcome = await h.Events.HandleAsync(In(chat, 7, fromMe: fromMe, senderIsBot: senderIsBot, topic: topic9 ? 9 : null), false, Ct);
        Assert.True(expected == outcome, name);
        Assert.Equal(expected == EventOutcome.Emitted ? 1 : 0, h.Notifier.Events.Count);
    }

    [Fact]
    public async Task Outgoing_event_carries_the_final_id_and_dedupes_by_it()
    {
        var h = H([.. FriendNotify, ("Events__IncludeOutgoing", "true")]);
        // The adapter only forwards the final message (updateMessageSendSucceeded), never the temporary one.
        Assert.Equal(EventOutcome.Emitted, await h.Events.HandleAsync(In(111, 4242, fromMe: true), false, Ct));
        var meta = Assert.Single(h.Notifier.Events).Meta;
        Assert.Equal("4242", meta["message_id"]);
        Assert.Equal("true", meta["from_me"]);
        Assert.Equal(4242, h.EventState.Saved[111]);
        Assert.Equal(EventOutcome.Emitted, await h.Events.HandleAsync(In(111, 4243), false, Ct));
    }

    [Fact]
    public async Task Global_read_switch_off_blocks_events()
    {
        var h = new Harness(("Enabled", "true"), ("Events__Enabled", "true"), ("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Notify", "true"));
        h.Telegram.Add(Friend);
        Assert.Equal(EventOutcome.Skipped, await h.Events.HandleAsync(In(111, 5), false, Ct));
    }

    [Fact]
    public async Task Since_days_apply_to_events()
    {
        var h = H([.. FriendNotify, ("Chats__0__SinceDays", "1")]);
        Assert.Equal(EventOutcome.Skipped, await h.Events.HandleAsync(In(111, 5, minutesAgo: 2 * 24 * 60), false, Ct));
    }

    [Fact]
    public async Task Duplicates_are_skipped_and_state_is_saved()
    {
        var h = H(FriendNotify);
        Assert.Equal(EventOutcome.Emitted, await h.Events.HandleAsync(In(111, 5), false, Ct));
        Assert.Equal(EventOutcome.Skipped, await h.Events.HandleAsync(In(111, 5), false, Ct));
        Assert.Equal(EventOutcome.Skipped, await h.Events.HandleAsync(In(111, 4), false, Ct));
        Assert.Equal(5, h.EventState.Saved[111]);
    }

    [Fact]
    public async Task Meta_carries_the_documented_string_attributes()
    {
        var h = H([("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Notify", "true"), ("Chats__0__Write", "draft"), ("Features__Send", "true")]);
        await h.Events.HandleAsync(In(-100222, 9, topic: 3, replyTo: 8, media: new TgMedia(1, 10, "photo", null, null)), false, Ct);
        var meta = Assert.Single(h.Notifier.Events).Meta;
        Assert.Equal("-100222", meta["chat_id"]);
        Assert.Equal("Dev Team", meta["chat_title"]);
        Assert.Equal("group", meta["chat_type"]);
        Assert.Equal("9", meta["message_id"]);
        Assert.Equal("42", meta["sender_id"]);
        Assert.Equal("Alice", meta["sender_name"]);
        Assert.Equal("alice", meta["sender_username"]);
        Assert.Equal("2030-01-15T12:00:00Z", meta["ts"]);
        Assert.Equal("3", meta["topic_id"]);
        Assert.Equal("8", meta["reply_to_message_id"]);
        Assert.Equal("true", meta["has_media"]);
        Assert.Equal("draft", meta["write_mode"]);
        Assert.False(meta.ContainsKey("catch_up"));
    }

    [Theory]
    [InlineData("on", "true", "on")]
    [InlineData("on", "false", "off")]  // features.send off
    [InlineData("off", "true", "off")]
    public async Task Write_mode_reflects_what_a_reply_may_do(string write, string send, string expected)
    {
        var h = H([.. FriendNotify, ("Chats__0__Write", write), ("Features__Send", send)]);
        await h.Events.HandleAsync(In(111, 5), false, Ct);
        Assert.Equal(expected, Assert.Single(h.Notifier.Events).Meta["write_mode"]);
    }

    [Fact]
    public async Task Text_is_truncated()
    {
        var h = H([.. FriendNotify, ("Events__MaxTextChars", "10")]);
        await h.Events.HandleAsync(In(111, 5, new string('x', 25)), false, Ct);
        var content = Assert.Single(h.Notifier.Events).Content;
        Assert.Contains("\nxxxxxxxxxx\n[truncated: 25 characters in total]", content, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 11), content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limit_drops_and_emits_one_summary_per_window()
    {
        var h = H([.. FriendNotify, ("Events__MaxPerMinute", "2")]);
        for (var i = 1; i <= 5; i++) await h.Events.HandleAsync(In(111, i), false, Ct);
        Assert.Equal(2, h.Notifier.Events.Count);
        await h.Events.FlushIfDueAsync(Ct);
        Assert.Equal(2, h.Notifier.Events.Count); // window not over yet
        h.Time.Advance(TimeSpan.FromSeconds(61));
        await h.Events.FlushIfDueAsync(Ct);
        await h.Events.FlushIfDueAsync(Ct);
        var summary = h.Notifier.Events[2];
        Assert.Contains("3 more message(s) dropped by rate limit", summary.Content, StringComparison.Ordinal);
        Assert.Equal("dropped", summary.Meta["kind"]);
        Assert.Equal(3, h.Notifier.Events.Count);
        var dropped = Assert.Single(h.Audit.Entries, e => e.Tool == "event_dropped");
        Assert.Equal(3, dropped.Count);
    }

    [Fact]
    public async Task Audit_has_one_text_free_record_per_event_and_fails_closed()
    {
        var h = H(FriendNotify);
        await h.Events.HandleAsync(In(111, 5, "secret words"), false, Ct);
        var e = Assert.Single(h.Audit.Entries);
        Assert.Equal("event", e.Tool);
        Assert.Equal(111, e.ChatId);
        Assert.Equal("message 5, length 12", e.Reason);
        Assert.StartsWith("chats[0]", e.Rule, StringComparison.Ordinal);

        h.Audit.FailWrites = true;
        Assert.Equal(EventOutcome.AuditFailed, await h.Events.HandleAsync(In(111, 6, "secret words"), false, Ct));
        Assert.Single(h.Notifier.Events);
    }

    [Fact]
    public async Task Catch_up_emits_recent_unseen_messages_oldest_first()
    {
        var h = H([.. FriendNotify, ("Chats__1__Id", "-100222"), ("Chats__1__Read", "true"), ("Chats__1__Notify", "true"), ("Events__CatchUpMinutes", "60")]);
        h.Telegram.Add(Friend, In(111, 1, "too old", minutesAgo: 120), In(111, 2, "seen", minutesAgo: 50), In(111, 3, "new a", minutesAgo: 30));
        h.Telegram.Add(Group, In(-100222, 10, "new b", minutesAgo: 40), In(-100222, 11, "new c", minutesAgo: 10));
        h.Telegram.Add(Channel, In(-100444, 30, "not notify", minutesAgo: 5));
        await h.EventState.SaveAsync(new Dictionary<long, long> { [111] = 2 }, Ct);

        Assert.Equal(3, await h.Events.CatchUpAsync(Ct));
        Assert.Equal(["new b", "new a", "new c"], h.Notifier.Events.Select(e => e.Content.Split('\n')[1]));
        Assert.All(h.Notifier.Events, e => Assert.Equal("true", e.Meta["catch_up"]));
        Assert.Equal(3, h.EventState.Saved[111]);
        Assert.Equal(11, h.EventState.Saved[-100222]);
        Assert.All(h.Audit.Entries, e => Assert.Equal("event_catch_up", e.Tool));
    }

    [Fact]
    public async Task Catch_up_is_off_by_default_and_capped()
    {
        var off = H(FriendNotify);
        off.Telegram.Add(Friend, In(111, 1, minutesAgo: 1));
        Assert.Equal(0, await off.Events.CatchUpAsync(Ct));

        var h = H([.. FriendNotify, ("Events__CatchUpMinutes", "600"), ("Events__MaxPerMinute", "1000")]);
        h.Telegram.Add(Friend, [.. Enumerable.Range(1, 80).Select(i => In(111, i, $"m{i}", minutesAgo: 100 - i))]);
        Assert.Equal(InboundEventProcessor.CatchUpCap, await h.Events.CatchUpAsync(Ct));
        Assert.Equal("m80", h.Notifier.Events[^1].Content.Split('\n')[1]);
    }
}
