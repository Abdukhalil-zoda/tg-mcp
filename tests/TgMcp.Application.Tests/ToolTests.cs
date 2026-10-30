using System.Text.Json;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;
using static TgMcp.Application.Tests.Harness;

namespace TgMcp.Application.Tests;

public class ToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TgChat Friend = new(111, "Friend", ["friend"], ChatKind.Private);
    private static readonly TgChat Team = new(-100222, "Dev Team", [], ChatKind.Group, IsForum: true);
    private static readonly TgChat Bank = new(333, "MyBank", ["mybank_bot"], ChatKind.Bot);
    private static readonly TgChat News = new(-100444, "News", ["news"], ChatKind.Channel);

    private static readonly (string, string)[] Base =
    [
        ("Enabled", "true"), ("Features__Read", "true"), ("Features__Search", "true"), ("Features__MediaDownload", "true"),
    ];

    private static Harness H(params (string, string)[] extra)
    {
        var h = new Harness([.. Base, .. extra]);
        h.Telegram.Add(Friend, Msg(111, 1, "hello old", 10), Msg(111, 2, "hello", 2), Msg(111, 3, "latest news", 0.5));
        h.Telegram.Add(Team, Msg(-100222, 10, "general", 1, topic: 1), Msg(-100222, 11, "release plan", 1, topic: 5),
            Msg(-100222, 12, "secret topic", 1, topic: 9),
            Msg(-100222, 13, "big file", 1, topic: 5, media: new TgMedia(77, 80L * 1024 * 1024, "document", "big.zip", "application/zip")),
            Msg(-100222, 14, "small file", 1, topic: 5, media: new TgMedia(78, 1000, "document", "a.pdf", "application/pdf")),
            Msg(-100222, 15, "no size", 1, topic: 5, media: new TgMedia(79, 0, "document", "x.bin", null)));
        h.Telegram.Add(Bank, Msg(333, 20, "your login code is 12345 hello"));
        h.Telegram.Add(News, Msg(-100444, 30, "hello world news"));
        return h;
    }

    private static JsonElement Json(ToolResult r)
    {
        Assert.False(r.IsError, r.Text);
        return JsonDocument.Parse(r.Text).RootElement;
    }

    private static long[] Ids(ToolResult r) =>
        [.. Json(r).GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetInt64())];

    // ------------------------------------------------------------- global gates

    [Fact]
    public async Task Disabled_server_never_touches_telegram()
    {
        var h = new Harness(("Features__Read", "true"), ("Defaults__Read", "true"));
        h.Telegram.Add(Friend, Msg(111, 1, "x"));
        var r = await h.Read.RunAsync("111", 10, 0, Ct);
        Assert.True(r.IsError);
        Assert.Contains("\"enabled\" is not true", r.Text, StringComparison.Ordinal);
        Assert.Equal(0, h.Telegram.ReadyCalls);
        Assert.Equal(0, h.Telegram.Calls);
        Assert.False(h.LastAudit.Allowed);
    }

    [Fact]
    public async Task Invalid_config_denies_every_tool()
    {
        var h = H(("Defaults__Read", "true"), ("Typo", "1"));
        var results = new[]
        {
            await h.ListChats.RunAsync(10, Ct),
            await h.Read.RunAsync("111", 10, 0, Ct),
            await h.Topic.RunAsync("-100222", 5, 10, 0, Ct),
            await h.Get.RunAsync("111", 2, Ct),
            await h.SearchChat.RunAsync("111", "hello", 10, 0, Ct),
            await h.SearchAll.RunAsync("hello", 10, Ct),
            await h.Download.RunAsync("-100222", 14, Ct),
        };
        Assert.All(results, r => Assert.True(r.IsError));
        Assert.All(results, r => Assert.Contains("config invalid", r.Text, StringComparison.Ordinal));
        Assert.Equal(0, h.Telegram.Calls);
        Assert.Equal(7, h.Audit.Entries.Count);
    }

    [Fact]
    public async Task Global_switch_off_blocks_tool_before_telegram()
    {
        var h = new Harness(("Enabled", "true"), ("Features__Read", "true"), ("Defaults__Read", "true"), ("Defaults__Features", "search"));
        h.Telegram.Add(Friend, Msg(111, 1, "hello"));
        var r = await h.SearchChat.RunAsync("111", "hello", 10, 0, Ct);
        Assert.True(r.IsError);
        Assert.Contains("\"features.search\" is off", r.Text, StringComparison.Ordinal);
        Assert.Equal(0, h.Telegram.Calls);
    }

    [Fact]
    public async Task Not_logged_in_is_a_clear_error()
    {
        var h = H(("Defaults__Read", "true"));
        h.Telegram.Unavailable = "not logged in: the owner must run `tgmcp login` first";
        var r = await h.Read.RunAsync("111", 10, 0, Ct);
        Assert.True(r.IsError);
        Assert.Contains("not logged in", r.Text, StringComparison.Ordinal);
        Assert.Contains("unavailable", h.LastAudit.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limit_refuses_and_refills()
    {
        var h = H(("Defaults__Read", "true"), ("Limits__RatePerMinute", "2"));
        Assert.False((await h.Read.RunAsync("111", 1, 0, Ct)).IsError);
        Assert.False((await h.Read.RunAsync("111", 1, 0, Ct)).IsError);
        var r = await h.Read.RunAsync("111", 1, 0, Ct);
        Assert.True(r.IsError);
        Assert.Contains("rate limit", r.Text, StringComparison.Ordinal);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.False((await h.Read.RunAsync("111", 1, 0, Ct)).IsError);
    }

    // ------------------------------------------------------------- read_messages

    [Fact]
    public async Task Read_messages_returns_contract_fields_and_note()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Read", "true"));
        var root = Json(await h.Read.RunAsync("111", 10, 0, Ct));
        Assert.Equal(ToolResult.ThirdPartyNote, root.GetProperty("note").GetString());
        var m = root.GetProperty("messages")[0];
        foreach (var f in new[] { "chat", "id", "date_utc", "sender", "from_me", "text", "has_media" })
            Assert.True(m.TryGetProperty(f, out _), f);
        Assert.Equal(3, m.GetProperty("id").GetInt64());
        Assert.Equal("2030-01-15T00:00:00Z", m.GetProperty("date_utc").GetString());
    }

    [Fact]
    public async Task Read_messages_needs_read()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Write", "on"));
        var r = await h.Read.RunAsync("111", 10, 0, Ct);
        Assert.True(r.IsError);
        Assert.Contains("Chat 111", r.Text, StringComparison.Ordinal);
        Assert.Contains("rule chats[0] has \"read\": false", r.Text, StringComparison.Ordinal);
        Assert.Equal("read=false", h.LastAudit.Reason);
        Assert.Equal(111, h.LastAudit.ChatId);
        Assert.False(h.LastAudit.Allowed);
    }

    [Fact]
    public async Task Read_messages_deny_wins()
    {
        var h = H(("Defaults__Read", "true"), ("Deny__0__Type", "bot"));
        var r = await h.Read.RunAsync("333", 10, 0, Ct);
        Assert.True(r.IsError);
        Assert.Equal(Denials.HardDeny, r.Text);
        Assert.StartsWith("deny[0]", h.LastAudit.Rule, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_messages_respects_since_days()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__SinceDays", "7"));
        Assert.Equal(new long[] { 3L, 2L }, Ids(await h.Read.RunAsync("111", 50, 0, Ct)));
    }

    [Fact]
    public async Task Read_messages_clamps_to_max_per_call()
    {
        var h = H(("Defaults__Read", "true"), ("Limits__MaxMessagesPerCall", "2"));
        Assert.Equal(2, Ids(await h.Read.RunAsync("111", 1000, 0, Ct)).Length);
    }

    [Fact]
    public async Task Read_messages_filters_topics()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Topics", "5"));
        var ids = Ids(await h.Read.RunAsync("-100222", 50, 0, Ct));
        Assert.Equal([15L, 14L, 13L, 11L], ids);
    }

    [Fact]
    public async Task Read_messages_resolves_username()
    {
        var h = H(("Chats__0__Username", "friend"), ("Chats__0__Read", "true"));
        Assert.NotEmpty(Ids(await h.Read.RunAsync("@Friend", 10, 0, Ct)));
        var bad = await h.Read.RunAsync("not a chat!", 10, 0, Ct);
        Assert.True(bad.IsError);
        Assert.Contains("bad argument", bad.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_messages_pages_from_message_id()
    {
        var h = H(("Defaults__Read", "true"));
        Assert.Equal(new long[] { 2L, 1L }, Ids(await h.Read.RunAsync("111", 10, 2, Ct)));
    }

    // ------------------------------------------------------------- actionable denials

    [Fact]
    public async Task Unmatched_chat_gets_a_json_rule_snippet()
    {
        var h = H(("Chats__0__Id", "-100444"), ("Chats__0__Read", "true"), ("Chats__7__Id", "-100999"), ("Chats__7__Read", "true"));
        var r = await h.Read.RunAsync("@friend", 10, 0, Ct);
        Assert.True(r.IsError);
        Assert.Equal("Chat 111 (\"Friend\", @friend) is not allowed. To grant read-only access add to \"chats\": { \"id\": 111, \"name\": \"Friend\", \"read\": true }", r.Text);
        Assert.Equal("defaults", h.LastAudit.Rule);
        Assert.DoesNotContain("Friend", h.LastAudit.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hint_refers_to_the_rule_by_its_human_name()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Name", "Best friend"), ("Chats__0__Read", "false"));
        var r = await h.Read.RunAsync("111", 10, 0, Ct);
        Assert.Contains("rule chats[0] (\"Best friend\") has \"read\": false", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_feature_hint_keeps_existing_features()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Features", "search"));
        var r = await h.Download.RunAsync("-100222", 14, Ct);
        Assert.Contains("set \"features\": [\"media_download\", \"search\"] in rule chats[0]", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_feature_without_rule_suggests_a_full_rule()
    {
        var h = H(("Defaults__Read", "true"));
        var r = await h.SearchChat.RunAsync("111", "hello", 10, 0, Ct);
        Assert.Contains("add to \"chats\": { \"id\": 111, \"name\": \"Friend\", \"read\": true, \"features\": [\"search\"] }", r.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Deny__0__Type", "bot")]
    [InlineData("Deny__0__Id", "333")]
    [InlineData("Deny__0__Username", "mybank_bot")]
    public async Task Hard_deny_reveals_nothing(string key, string value)
    {
        var h = H(("Chats__0__Id", "333"), ("Chats__0__Read", "true"), (key, value));
        foreach (var r in new[]
        {
            await h.Read.RunAsync("333", 10, 0, Ct),
            await h.Get.RunAsync("@mybank_bot", 20, Ct),
            await h.Topic.RunAsync("333", 1, 10, 0, Ct),
        })
        {
            Assert.True(r.IsError);
            Assert.Equal(Denials.HardDeny, r.Text);
        }
    }

    [Fact]
    public async Task Since_days_denial_names_the_setting()
    {
        var h = H(("Defaults__Read", "true"), ("Defaults__SinceDays", "7"));
        var r = await h.Get.RunAsync("111", 1, Ct);
        Assert.Contains("7-day limit for Chat 111", r.Text, StringComparison.Ordinal);
        Assert.Contains("\"defaults.sinceDays\"", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folder_failure_does_not_echo_folder_names()
    {
        var h = H(("Defaults__Read", "true"), ("Deny__0__Folder", "Finance"));
        h.Telegram.Folders = new() { ["Other"] = new HashSet<long>() };
        var r = await h.Read.RunAsync("111", 10, 0, Ct);
        Assert.True(r.IsError);
        Assert.DoesNotContain("Finance", r.Text, StringComparison.Ordinal);
        Assert.Contains("\"folder\" rules in \"deny\"", r.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- read_topic

    [Fact]
    public async Task Read_topic_requires_topic_in_list()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Topics", "5"));
        var denied = await h.Topic.RunAsync("-100222", 9, 10, 0, Ct);
        Assert.True(denied.IsError);
        Assert.Contains("Topic 9", denied.Text, StringComparison.Ordinal);
        Assert.Contains("add 9 to \"topics\" in rule chats[0]", denied.Text, StringComparison.Ordinal);
        Assert.Equal(new long[] { 15L, 14L, 13L, 11L }, Ids(await h.Topic.RunAsync("-100222", 5, 10, 0, Ct)));
    }

    [Fact]
    public async Task Read_topic_without_topics_list_allows_any_topic()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"));
        Assert.Equal(new long[] { 12L }, Ids(await h.Topic.RunAsync("-100222", 9, 10, 0, Ct)));
    }

    [Fact]
    public async Task Read_topic_needs_read()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Features", "search"));
        Assert.True((await h.Topic.RunAsync("-100222", 5, 10, 0, Ct)).IsError);
    }

    // ------------------------------------------------------------- get_message

    [Fact]
    public async Task Get_message_applies_since_days_and_topics()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__SinceDays", "7"),
                  ("Chats__1__Id", "-100222"), ("Chats__1__Read", "true"), ("Chats__1__Topics", "5"));
        Assert.Equal(new long[] { 2L }, Ids(await h.Get.RunAsync("111", 2, Ct)));
        Assert.Contains("\"sinceDays\" in rule chats[0]", (await h.Get.RunAsync("111", 1, Ct)).Text, StringComparison.Ordinal);
        Assert.Contains("Topic 9 of Chat -100222", (await h.Get.RunAsync("-100222", 12, Ct)).Text, StringComparison.Ordinal);
        Assert.Equal(new long[] { 11L }, Ids(await h.Get.RunAsync("-100222", 11, Ct)));
        var missing = await h.Get.RunAsync("111", 999, Ct);
        Assert.True(missing.IsError);
        Assert.True(h.LastAudit.Allowed);
    }

    [Fact]
    public async Task Get_message_needs_read()
    {
        var h = H();
        Assert.True((await h.Get.RunAsync("111", 2, Ct)).IsError);
    }

    // ------------------------------------------------------------- search_chat

    [Fact]
    public async Task Search_chat_needs_search_feature()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Read", "true"));
        var r = await h.SearchChat.RunAsync("111", "hello", 10, 0, Ct);
        Assert.True(r.IsError);
        Assert.Contains("'search' feature", r.Text, StringComparison.Ordinal);
        Assert.Contains("\"features\": [\"search\"] in rule chats[0]", r.Text, StringComparison.Ordinal);

        var ok = H(("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Features", "search"));
        Assert.Equal(new long[] { 2L, 1L }, Ids(await ok.SearchChat.RunAsync("111", "hello", 10, 0, Ct)));
    }

    [Fact]
    public async Task Search_chat_respects_since_days()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Features", "search"), ("Chats__0__SinceDays", "5"));
        Assert.Equal(new long[] { 2L }, Ids(await h.SearchChat.RunAsync("111", "hello", 10, 0, Ct)));
    }

    [Fact]
    public async Task Search_chat_rejects_empty_query()
    {
        var h = H(("Defaults__Read", "true"), ("Defaults__Features", "search"));
        Assert.True((await h.SearchChat.RunAsync("111", "  ", 10, 0, Ct)).IsError);
    }

    // ------------------------------------------------------------- search_all

    [Fact]
    public async Task Search_all_drops_denied_chats_silently()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Features", "search"),
                  ("Chats__1__Id", "-100444"), ("Chats__1__Read", "true"), // no search feature
                  ("Deny__0__Type", "bot"));
        var r = await h.SearchAll.RunAsync("hello", 50, Ct);
        var root = Json(r);
        var chats = root.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("chat").GetInt64()).Distinct().ToList();
        Assert.Equal([111L], chats);
        Assert.DoesNotContain("333", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("-100444", r.Text, StringComparison.Ordinal);
        Assert.Contains("dropped", h.LastAudit.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_all_applies_per_chat_since_days()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Features", "search"), ("Chats__0__SinceDays", "5"));
        Assert.Equal(new long[] { 2L }, Ids(await h.SearchAll.RunAsync("hello", 50, Ct)));
    }

    [Fact]
    public async Task Search_all_needs_global_search()
    {
        var h = new Harness(("Enabled", "true"), ("Features__Read", "true"), ("Defaults__Read", "true"), ("Defaults__Features", "search"));
        Assert.True((await h.SearchAll.RunAsync("hello", 50, Ct)).IsError);
    }

    // ------------------------------------------------------------- download_media

    [Fact]
    public async Task Download_media_needs_feature()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"));
        var r = await h.Download.RunAsync("-100222", 14, Ct);
        Assert.True(r.IsError);
        Assert.Contains("media_download", r.Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Downloads);
    }

    [Fact]
    public async Task Download_media_enforces_size_cap_before_downloading()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Features", "media_download"));
        Assert.Contains("limits.maxMediaMb", (await h.Download.RunAsync("-100222", 13, Ct)).Text, StringComparison.Ordinal);
        Assert.Contains("size is unknown", (await h.Download.RunAsync("-100222", 15, Ct)).Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Downloads);
    }

    [Fact]
    public async Task Download_media_rechecks_size_after_download()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Features", "media_download"));
        h.Telegram.DownloadedSize = 60L * 1024 * 1024;
        Assert.True((await h.Download.RunAsync("-100222", 14, Ct)).IsError);
    }

    [Fact]
    public async Task Download_media_stores_file()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Features", "media_download"));
        var root = Json(await h.Download.RunAsync("-100222", 14, Ct));
        Assert.Equal("/data/media/-100222_14_a.pdf", root.GetProperty("path").GetString());
        Assert.False(root.TryGetProperty("host_path", out _));
        Assert.Equal(ToolResult.ThirdPartyNote, root.GetProperty("note").GetString());
        Assert.Equal([78], h.Telegram.Downloads);
    }

    [Fact]
    public async Task Download_media_applies_retention_before_downloading()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Features", "media_download"),
                  ("Limits__MediaRetentionDays", "3"));
        await h.Download.RunAsync("-100222", 14, Ct);
        Assert.Equal([3], h.Media.Cleanups);
    }

    [Fact]
    public async Task Download_media_reports_host_path_when_configured()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Features", "media_download"));
        h.Media.HostDir = "/var/lib/tgmcp/media";
        var root = Json(await h.Download.RunAsync("-100222", 14, Ct));
        Assert.Equal("/var/lib/tgmcp/media/-100222_14_a.pdf", root.GetProperty("host_path").GetString());
    }

    [Fact]
    public async Task Download_media_without_media_fails()
    {
        var h = H(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Features", "media_download"));
        Assert.Contains("no media", (await h.Download.RunAsync("-100222", 11, Ct)).Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- list_chats

    [Fact]
    public async Task List_chats_allowed_only_shows_readable()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__1__Id", "-100444"), ("Chats__1__Write", "draft"));
        var chats = Json(await h.ListChats.RunAsync(100, Ct)).GetProperty("chats");
        Assert.Equal([111L], chats.EnumerateArray().Select(c => c.GetProperty("id").GetInt64()));
        Assert.True(chats[0].GetProperty("read").GetBoolean());
    }

    [Fact]
    public async Task List_chats_names_all_hides_denied()
    {
        var h = H(("Limits__ListChats", "names_all"), ("Deny__0__Type", "bot"), ("Chats__0__Id", "111"), ("Chats__0__Read", "true"));
        var chats = Json(await h.ListChats.RunAsync(100, Ct)).GetProperty("chats").EnumerateArray().ToList();
        Assert.Equal(3, chats.Count);
        Assert.DoesNotContain(chats, c => c.GetProperty("id").GetInt64() == 333);
        Assert.True(chats.Single(c => c.GetProperty("id").GetInt64() == 111).GetProperty("readable").GetBoolean());
        Assert.False(chats.Single(c => c.GetProperty("id").GetInt64() == -100444).GetProperty("readable").GetBoolean());
    }

    [Fact]
    public async Task List_chats_off_is_refused()
    {
        var h = H(("Limits__ListChats", "off"), ("Defaults__Read", "true"));
        var r = await h.ListChats.RunAsync(100, Ct);
        Assert.True(r.IsError);
        Assert.Contains("\"limits.listChats\" is \"off\"", r.Text, StringComparison.Ordinal);
        Assert.Equal(0, h.Telegram.Calls);
    }

    // ------------------------------------------------------------- folders & audit

    [Fact]
    public async Task Folder_deny_resolved_through_telegram()
    {
        var h = H(("Defaults__Read", "true"), ("Deny__0__Folder", "Finance"));
        h.Telegram.Folders = new() { ["Finance"] = new HashSet<long> { 111 } };
        Assert.True((await h.Read.RunAsync("111", 10, 0, Ct)).IsError);
        Assert.False((await h.Read.RunAsync("-100444", 10, 0, Ct)).IsError);
    }

    [Fact]
    public async Task Folder_deny_fails_closed_when_folders_unavailable()
    {
        var h = H(("Defaults__Read", "true"), ("Deny__0__Folder", "Finance"));
        h.Telegram.Folders = null;
        var r = await h.Read.RunAsync("-100444", 10, 0, Ct);
        Assert.True(r.IsError);
        Assert.Contains("folder", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_never_contains_message_text_or_query()
    {
        var h = H(("Defaults__Read", "true"), ("Defaults__Features", "search"));
        await h.Read.RunAsync("111", 10, 0, Ct);
        await h.SearchChat.RunAsync("111", "hello", 10, 0, Ct);
        await h.SearchAll.RunAsync("latest", 10, Ct);
        var all = string.Join("\n", h.Audit.Entries.Select(e => $"{e.Tool} {e.Rule} {e.Reason}"));
        Assert.DoesNotContain("hello", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("latest", all, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, h.Audit.Entries.Count);
        Assert.Equal(3, h.Audit.Entries[0].Count);
    }

    [Fact]
    public async Task Unavailable_audit_denies_before_any_telegram_call()
    {
        var h = H(("Defaults__Read", "true"));
        h.Audit.Available = false;
        var r = await h.Read.RunAsync("111", 10, 0, Ct);
        Assert.True(r.IsError);
        Assert.Equal(ToolRunner.AuditUnavailable, r.Text);
        Assert.Equal(0, h.Telegram.ReadyCalls);
        Assert.Equal(0, h.Telegram.Calls);
    }

    [Fact]
    public async Task Failed_audit_write_withholds_the_result()
    {
        var h = H(("Defaults__Read", "true"));
        h.Audit.FailWrites = true;
        var r = await h.Read.RunAsync("111", 10, 0, Ct);
        Assert.True(r.IsError);
        Assert.Equal(ToolRunner.AuditUnavailable, r.Text);
        Assert.DoesNotContain("hello", r.Text, StringComparison.Ordinal);
        Assert.Empty(h.Audit.Entries);
    }

    [Fact]
    public async Task Telegram_errors_are_reported_and_audited()
    {
        var h = H(("Defaults__Read", "true"));
        var r = await h.Read.RunAsync("999", 10, 0, Ct);
        Assert.True(r.IsError);
        Assert.Contains("telegram error 400", h.LastAudit.Reason, StringComparison.Ordinal);
    }
}
