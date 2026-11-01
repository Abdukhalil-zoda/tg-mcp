using System.Text.Json;
using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;

namespace TgMcp.Application.Tests;

public class AddMemberToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TgChat Group = new(-1001234567890, "Example Group", ["example_group"], ChatKind.Group);
    private static readonly TgChat Friend = new(111, "Alice", ["alice"], ChatKind.Private);
    private static readonly TgUser Bob = new(222, "Bob", "bob_user", false);

    private static Harness H(params (string, string)[] extra)
    {
        var h = new Harness([("Enabled", "true"), ("Features__AddMember", "true"), .. extra]);
        Assert.True(h.Policy.Config.IsValid, string.Join("; ", h.Policy.Config.Errors));
        h.Telegram.Add(Group);
        h.Telegram.Add(Friend);
        h.Telegram.Users[Bob.Id] = Bob;
        return h;
    }

    private static JsonElement Json(ToolResult r)
    {
        Assert.False(r.IsError, r.Text);
        return JsonDocument.Parse(r.Text).RootElement;
    }

    [Fact]
    public async Task Feature_off_and_mode_off_are_denied_before_telegram()
    {
        var noFeature = new Harness(("Enabled", "true"), ("AddMember__Mode", "on"), ("AddMember__Chats", "@example_group"));
        var r = await noFeature.AddMember.RunAsync("@example_group", "@bob_user", Ct);
        Assert.Contains("\"features.addMember\" is off", r.Text, StringComparison.Ordinal);
        Assert.Equal(0, noFeature.Telegram.Calls);

        var off = H();
        r = await off.AddMember.RunAsync("@example_group", "@bob_user", Ct);
        Assert.True(r.IsError);
        Assert.Contains("\"addMember\": { \"mode\": \"draft\" }", r.Text, StringComparison.Ordinal);
        Assert.Equal(0, off.Telegram.Calls);
        Assert.False(off.LastAudit.Allowed);
        Assert.Equal("add_member", off.LastAudit.Tool);
    }

    [Fact]
    public async Task On_adds_only_to_listed_chats()
    {
        var h = H(("AddMember__Mode", "on"), ("AddMember__Chats", "@example_group"));
        var root = Json(await h.AddMember.RunAsync("-1001234567890", "@Bob_User", Ct));
        Assert.Equal("added", root.GetProperty("status").GetString());
        Assert.Equal(222, root.GetProperty("user_id").GetInt64());
        Assert.Equal((Group.Id, Bob.Id), Assert.Single(h.Telegram.Added));
        Assert.Equal("user 222, mode on, added", h.LastAudit.Reason);
        Assert.Equal(Group.Id, h.LastAudit.ChatId);

        var other = H(("AddMember__Mode", "on"), ("AddMember__Chats", "-100555"));
        var r = await other.AddMember.RunAsync("@example_group", "222", Ct);
        Assert.Contains("is not in \"addMember.chats\"", r.Text, StringComparison.Ordinal);
        Assert.Contains("add -1001234567890 to \"addMember.chats\"", r.Text, StringComparison.Ordinal);
        Assert.Empty(other.Telegram.Added);

        var empty = H(("AddMember__Mode", "on"));
        Assert.Contains("\"addMember.chats\" is empty", (await empty.AddMember.RunAsync("@example_group", "222", Ct)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deny_rules_on_chat_or_user_win()
    {
        var chatDenied = H(("AddMember__Mode", "on"), ("AddMember__Chats", "@example_group"), ("Deny__0__Id", "-1001234567890"));
        Assert.Equal(Denials.HardDeny, (await chatDenied.AddMember.RunAsync("@example_group", "222", Ct)).Text);
        var userDenied = H(("AddMember__Mode", "draft"), ("Deny__0__Username", "bob_user"));
        Assert.Equal(Denials.HardDeny, (await userDenied.AddMember.RunAsync("@example_group", "222", Ct)).Text);
        Assert.Empty(userDenied.DraftStore.Drafts);
        var bots = H(("AddMember__Mode", "draft"), ("Deny__0__Type", "bot"));
        bots.Telegram.Users[333] = new TgUser(333, "Some Bot", "some_bot", true);
        Assert.Equal(Denials.HardDeny, (await bots.AddMember.RunAsync("@example_group", "333", Ct)).Text);
    }

    [Fact]
    public async Task Bad_targets_and_users_are_rejected()
    {
        var h = H(("AddMember__Mode", "draft"));
        Assert.Contains("only be added to groups and channels", (await h.AddMember.RunAsync("111", "222", Ct)).Text, StringComparison.Ordinal);
        Assert.Contains("unknown user id", (await h.AddMember.RunAsync("@example_group", "999", Ct)).Text, StringComparison.Ordinal);
        Assert.Contains("no Telegram user with that username", (await h.AddMember.RunAsync("@example_group", "@nobody_here", Ct)).Text, StringComparison.Ordinal);
        Assert.StartsWith("bad argument:", (await h.AddMember.RunAsync("@example_group", "not valid!", Ct)).Text, StringComparison.Ordinal);
        Assert.Empty(h.DraftStore.Drafts);
    }

    [Fact]
    public async Task Draft_list_approve_and_reject()
    {
        var h = H(("AddMember__Mode", "draft"));
        var root = Json(await h.AddMember.RunAsync("@example_group", "@bob_user", Ct));
        Assert.Equal("draft", root.GetProperty("status").GetString());
        var id = root.GetProperty("draft_id").GetString()!;
        Assert.Contains($"tgmcp approve {id}", root.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(h.Telegram.Added);
        var draft = Assert.Single(await h.DraftCommands.ListAsync(Ct));
        Assert.Equal(Draft.AddMemberKind, draft.Kind);
        Assert.Equal(new PendingAddMember(Group.Id, Bob.Id, "@bob_user"), draft.AddMember);
        Assert.Equal($"user 222, mode draft, draft {id} saved", h.LastAudit.Reason);

        var approved = Json(await h.DraftCommands.ApproveAsync(id, Ct));
        Assert.Equal("added", approved.GetProperty("status").GetString());
        Assert.Single(h.Telegram.Added);
        Assert.Empty(h.DraftStore.Drafts);
        Assert.Equal("approve", h.LastAudit.Tool);
        Assert.Equal($"user 222, mode draft, draft {id} approved, added", h.LastAudit.Reason);

        var second = Json(await h.AddMember.RunAsync("@example_group", "222", Ct)).GetProperty("draft_id").GetString()!;
        Assert.True(await h.DraftCommands.RejectAsync(second, Ct));
        Assert.Equal("reject", h.LastAudit.Tool);
        Assert.Equal(Group.Id, h.LastAudit.ChatId);
        Assert.Single(h.Telegram.Added);
    }

    [Fact]
    public async Task Approve_rechecks_mode_and_deny_rules()
    {
        var h = H(("AddMember__Mode", "draft"));
        var id = Json(await h.AddMember.RunAsync("@example_group", "222", Ct)).GetProperty("draft_id").GetString()!;
        var off = H();
        await off.DraftStore.SaveAddMemberAsync("Example Group", h.DraftStore.Drafts[id].AddMember!, Ct);
        var r = await off.DraftCommands.ApproveAsync("00000001", Ct);
        Assert.Contains("Adding members is off", r.Text, StringComparison.Ordinal);
        Assert.Empty(off.Telegram.Added);
        Assert.Single(off.DraftStore.Drafts);

        var denied = H(("AddMember__Mode", "draft"), ("Deny__0__Id", "222"));
        await denied.DraftStore.SaveAddMemberAsync("Example Group", h.DraftStore.Drafts[id].AddMember!, Ct);
        Assert.Equal(Denials.HardDeny, (await denied.DraftCommands.ApproveAsync("00000001", Ct)).Text);
        Assert.Empty(denied.Telegram.Added);
    }

    [Theory]
    [InlineData("USER_PRIVACY_RESTRICTED", "privacy settings")]
    [InlineData("CHAT_ADMIN_REQUIRED", "lack the right to add members")]
    [InlineData("Not enough rights to invite members to the group chat", "lack the right to add members")]
    [InlineData("PEER_FLOOD", "Stop adding members for at least a day")]
    [InlineData("USER_NOT_MUTUAL_CONTACT", "mutual contact")]
    public async Task Telegram_errors_get_friendly_messages(string tdError, string expected)
    {
        var h = H(("AddMember__Mode", "on"), ("AddMember__Chats", "@example_group"));
        h.Telegram.AddFailure = new TelegramCallException(403, tdError);
        var r = await h.AddMember.RunAsync("@example_group", "222", Ct);
        Assert.True(r.IsError);
        Assert.Contains(expected, r.Text, StringComparison.Ordinal);
        Assert.StartsWith("user 222, mode on, telegram error ", h.LastAudit.Reason, StringComparison.Ordinal);
        Assert.Equal("telegram", h.LastAudit.Rule);
    }

    [Fact]
    public async Task Already_participant_is_success()
    {
        var h = H(("AddMember__Mode", "on"), ("AddMember__Chats", "@example_group"));
        h.Telegram.AddFailure = new TelegramCallException(400, "USER_ALREADY_PARTICIPANT");
        Assert.Equal("already_member", Json(await h.AddMember.RunAsync("@example_group", "222", Ct)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Hourly_and_daily_limits_apply_to_drafts_too()
    {
        var h = H(("AddMember__Mode", "draft"), ("Limits__AddsPerHour", "2"), ("Limits__AddsPerDay", "3"));
        Assert.False((await h.AddMember.RunAsync("@example_group", "222", Ct)).IsError);
        Assert.False((await h.AddMember.RunAsync("@example_group", "222", Ct)).IsError);
        Assert.Contains("limits.addsPerHour=2", (await h.AddMember.RunAsync("@example_group", "222", Ct)).Text, StringComparison.Ordinal);
        h.Time.Advance(TimeSpan.FromHours(1));
        Assert.False((await h.AddMember.RunAsync("@example_group", "222", Ct)).IsError);
        var r = await h.AddMember.RunAsync("@example_group", "222", Ct);
        Assert.Contains("limits.addsPerDay=3", r.Text, StringComparison.Ordinal);
        Assert.Contains("spread large batches over several days", r.Text, StringComparison.Ordinal);
        Assert.Equal(3, h.DraftStore.Drafts.Count);
    }
}

public class PaceAndJoinsPerDayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Daily_refusal_does_not_burn_an_hourly_token()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var p = new PacedLimiter(2, 2, time);
        Assert.Equal(PaceLimit.None, p.TryAcquire());
        Assert.Equal(PaceLimit.None, p.TryAcquire());
        Assert.Equal(PaceLimit.Day, p.TryAcquire());
        time.Advance(TimeSpan.FromHours(12)); // one daily token back, hourly bucket full again
        Assert.Equal(PaceLimit.None, p.TryAcquire());
        Assert.Equal(PaceLimit.Day, p.TryAcquire());
        var hourly = new PacedLimiter(1, 10, time);
        Assert.Equal(PaceLimit.None, hourly.TryAcquire());
        Assert.Equal(PaceLimit.Hour, hourly.TryAcquire());
    }

    [Fact]
    public async Task Joins_per_day_caps_join_chat()
    {
        var h = new Harness(("Enabled", "true"), ("Features__Join", "true"), ("Join__Mode", "draft"), ("Limits__JoinsPerHour", "5"), ("Limits__JoinsPerDay", "2"));
        h.Telegram.Previews["example_channel"] = new(-1001234567890, "Example Channel", "channel", ChatKind.Channel, ["example_channel"], 1, false, false);
        Assert.False((await h.Join.RunAsync("@example_channel", Ct)).IsError);
        Assert.False((await h.Join.RunAsync("@example_channel", Ct)).IsError);
        Assert.Contains("limits.joinsPerDay=2", (await h.Join.RunAsync("@example_channel", Ct)).Text, StringComparison.Ordinal);
    }
}

public class DirectoryToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Json(ToolResult r)
    {
        Assert.False(r.IsError, r.Text);
        return JsonDocument.Parse(r.Text).RootElement;
    }

    private static Harness Public(params (string, string)[] extra)
    {
        var h = new Harness([("Enabled", "true"), ("Features__SearchPublic", "true"), .. extra]);
        h.Telegram.PublicChats.Add(new TgPublicChat(-1001, "Dotnet News", ["dotnet_news"], ChatKind.Channel, "channel", 5000));
        h.Telegram.PublicChats.Add(new TgPublicChat(-1002, "Dotnet Chat", ["dotnet_chat"], ChatKind.Group, "supergroup", 800));
        h.Telegram.PublicChats.Add(new TgPublicChat(42, "Dotnet Person", ["dotnet_person"], ChatKind.Private, "private", 0));
        return h;
    }

    [Fact]
    public async Task Search_public_is_gated_and_returns_directory_data()
    {
        var off = new Harness(("Enabled", "true"));
        Assert.Contains("\"features.searchPublic\" is off", (await off.SearchPublic.RunAsync("dotnet", 10, Ct)).Text, StringComparison.Ordinal);
        Assert.Equal(0, off.Telegram.Calls);

        var h = Public();
        var root = Json(await h.SearchPublic.RunAsync("dotnet", 10, Ct));
        Assert.Equal(ToolResult.ThirdPartyNote, root.GetProperty("note").GetString());
        var results = root.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(3, results.Count);
        Assert.Equal("@dotnet_news", results[0].GetProperty("username").GetString());
        Assert.Equal(5000, results[0].GetProperty("member_count").GetInt32());
        Assert.False(results[2].TryGetProperty("member_count", out _)); // people have no member count
        Assert.Equal("private", results[2].GetProperty("type").GetString());
        Assert.DoesNotContain("dotnet", h.LastAudit.Reason, StringComparison.Ordinal); // the query is never audited
    }

    [Fact]
    public async Task Search_public_puts_the_exact_username_first_and_drops_denied()
    {
        var h = Public(("Deny__0__Type", "private"));
        var root = Json(await h.SearchPublic.RunAsync("@dotnet_chat", 10, Ct));
        var results = root.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(-1002, results[0].GetProperty("id").GetInt64());
        Assert.Single(results);
        var all = Json(await h.SearchPublic.RunAsync("dotnet", 10, Ct)).GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal("ok, 1 dropped by policy", h.LastAudit.Reason);
    }

    [Fact]
    public async Task Search_public_rate_limit()
    {
        var h = Public(("Limits__PublicSearchesPerHour", "1"));
        Assert.False((await h.SearchPublic.RunAsync("dotnet", 10, Ct)).IsError);
        Assert.Contains("limits.publicSearchesPerHour=1", (await h.SearchPublic.RunAsync("dotnet", 10, Ct)).Text, StringComparison.Ordinal);
        Assert.StartsWith("bad argument:", (await h.SearchPublic.RunAsync("  ", 10, Ct)).Text, StringComparison.Ordinal);
    }

    private static Harness Dir(params (string, string)[] extra)
    {
        var h = new Harness([("Enabled", "true"), ("Features__FindChats", "true"), .. extra]);
        h.Telegram.Directory.Add(new TgDirectoryChat(-1001, "Tashkent Devs", [], ChatKind.Group, "supergroup", 300, false, true, false));
        h.Telegram.Directory.Add(new TgDirectoryChat(-1002, "News Channel", ["uz_news"], ChatKind.Channel, "channel", 9000, true, false, false));
        h.Telegram.Directory.Add(new TgDirectoryChat(-1003, "Family", [], ChatKind.Group, "group", 5, false, true, true));
        return h;
    }

    [Fact]
    public async Task Find_chats_is_not_limited_by_read_rules_but_by_deny_rules()
    {
        var off = new Harness(("Enabled", "true"));
        Assert.Contains("\"features.findChats\" is off", (await off.FindChats.RunAsync(["devs"], null, 0, 10, Ct)).Text, StringComparison.Ordinal);

        var h = Dir(("Deny__0__Id", "-1003"));   // no read rules at all
        var root = Json(await h.FindChats.RunAsync(null, "any", 0, 100, Ct));
        Assert.Equal(2, root.GetProperty("total").GetInt32());
        var chats = root.GetProperty("chats").EnumerateArray().ToList();
        Assert.Equal("Tashkent Devs", chats[0].GetProperty("title").GetString());
        Assert.True(chats[0].GetProperty("can_add_members").GetBoolean());
        Assert.False(chats[0].GetProperty("can_include_in_folder_link").GetBoolean());
        Assert.True(chats[1].GetProperty("archived").GetBoolean());
        Assert.True(chats[1].GetProperty("can_include_in_folder_link").GetBoolean()); // public
        Assert.Equal("@uz_news", chats[1].GetProperty("username").GetString());
        Assert.False(chats[0].TryGetProperty("messages", out _));
        Assert.Equal(FindChatsTool.MaxScan, h.Telegram.LastScanMax);
    }

    [Fact]
    public async Task Find_chats_keywords_kind_and_paging()
    {
        var h = Dir();
        var root = Json(await h.FindChats.RunAsync(["DEVS", "uz_"], null, 0, 10, Ct));
        Assert.Equal(2, root.GetProperty("total").GetInt32());   // title match + username match
        root = Json(await h.FindChats.RunAsync(["devs", "news", "family"], "channel", 0, 10, Ct));
        Assert.Equal(-1002, Assert.Single(root.GetProperty("chats").EnumerateArray()).GetProperty("id").GetInt64());
        var page1 = Json(await h.FindChats.RunAsync([], "group", 0, 1, Ct));
        Assert.Equal(1, page1.GetProperty("next_offset").GetInt32());
        var page2 = Json(await h.FindChats.RunAsync([], "group", 1, 1, Ct));
        Assert.False(page2.TryGetProperty("next_offset", out _)); // last page
        Assert.Equal("Family", page2.GetProperty("chats")[0].GetProperty("title").GetString());
        Assert.StartsWith("bad argument:", (await h.FindChats.RunAsync([], "people", 0, 1, Ct)).Text, StringComparison.Ordinal);
        Assert.DoesNotContain("devs", h.Audit.Entries[0].Reason, StringComparison.OrdinalIgnoreCase);
    }
}

public class FolderToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Link = "https://t.me/addlist/AbCdEfGh12";

    private static JsonElement Json(ToolResult r)
    {
        Assert.False(r.IsError, r.Text);
        return JsonDocument.Parse(r.Text).RootElement;
    }

    private static Harness H(params (string, string)[] extra)
    {
        var h = new Harness([("Enabled", "true"), ("Features__Folders", "true"), .. extra]);
        Assert.True(h.Policy.Config.IsValid, string.Join("; ", h.Policy.Config.Errors));
        for (var i = 1; i <= 250; i++) h.Telegram.Add(new TgChat(-1000000000000 - i, $"Group {i}", [], ChatKind.Group));
        h.Telegram.Add(new TgChat(111, "Alice", ["alice"], ChatKind.Private));
        h.Telegram.FolderPreviews["AbCdEfGh12"] = new TgFolderPreview("Uz groups", false, [-1000000000001, -1000000000002, -1000000000003], [-1000000000004]);
        return h;
    }

    private static long[] Ids(int n) => [.. Enumerable.Range(1, n).Select(i => -1000000000000L - i)];

    private static void NoSlugInAudit(Harness h)
    {
        foreach (var e in h.Audit.Entries)
        {
            Assert.DoesNotContain("AbCdEfGh12", e.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("SecretSlug", e.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Off_is_denied_with_the_snippet()
    {
        var h = H();
        var r = await h.ShareFolder.RunAsync("Uz", Ids(3), Ct);
        Assert.Contains("\"features\": { \"folders\": true } and \"folders\": { \"mode\": \"draft\" }", r.Text, StringComparison.Ordinal);
        Assert.Contains("\"folders\": { \"mode\": \"draft\" }", (await h.JoinFolder.RunAsync(Link, false, Ct)).Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.CreatedFolders);
        Assert.Empty(h.Telegram.JoinedFolders);
        var noFeature = new Harness(("Enabled", "true"), ("Folders__Mode", "on"));
        Assert.Contains("\"features.folders\" is off", (await noFeature.ShareFolder.RunAsync("Uz", Ids(1), Ct)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_splits_into_folders_of_100_and_reports_exclusions()
    {
        var h = H(("Folders__Mode", "on"), ("Deny__0__Id", "-1000000000005"));
        h.Telegram.NotLinkable.Add(-1000000000007);
        var root = Json(await h.ShareFolder.RunAsync("Uzbekistan", [.. Ids(250), 111, 999], Ct));
        Assert.Equal("shared", root.GetProperty("status").GetString());
        var folders = root.GetProperty("folders").EnumerateArray().ToList();
        Assert.Equal(3, folders.Count);
        Assert.Equal(["Uzbekistan 1", "Uzbekistan 2", "Uzbekistan 3"], folders.Select(f => f.GetProperty("name").GetString()));
        Assert.All(h.Telegram.CreatedFolders.Values, f => Assert.True(f.Chats.Length <= 100));
        Assert.Equal(99, folders[0].GetProperty("included").GetArrayLength()); // one not linkable
        Assert.StartsWith("https://t.me/addlist/", folders[0].GetProperty("invite_link").GetString(), StringComparison.Ordinal);
        var excluded = root.GetProperty("excluded").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetInt64(), e => e.GetProperty("reason").GetString());
        Assert.Equal(Denials.HardDeny, excluded[-1000000000005]);
        Assert.Contains("people and bots", excluded[111], StringComparison.Ordinal);
        Assert.Contains("unknown chat id", excluded[999], StringComparison.Ordinal);
        Assert.Contains("invite-users right", excluded[-1000000000007], StringComparison.Ordinal);
        Assert.Contains("3 link(s) addlist#", h.LastAudit.Reason, StringComparison.Ordinal);
        NoSlugInAudit(h);
    }

    [Fact]
    public async Task Share_stops_at_a_telegram_limit_and_explains_it()
    {
        var h = H(("Folders__Mode", "on"));
        h.Telegram.CreateFolderFailure = new TelegramCallException(400, "CHATLISTS_TOO_MUCH");
        h.Telegram.FailCreateFolderAfter = 1;
        var root = Json(await h.ShareFolder.RunAsync("Many", Ids(250), Ct));
        Assert.Single(root.GetProperty("folders").EnumerateArray());
        Assert.Equal(150, root.GetProperty("not_processed").GetInt32());
        Assert.Contains("too many shared folders", root.GetProperty("error").GetString(), StringComparison.Ordinal);

        var none = H(("Folders__Mode", "on"));
        none.Telegram.CreateFolderFailure = new TelegramCallException(400, "DIALOG_FILTERS_TOO_MUCH");
        none.Telegram.FailCreateFolderAfter = 0;
        var r = await none.ShareFolder.RunAsync("Many", Ids(5), Ct);
        Assert.True(r.IsError);
        Assert.Contains("10 without Premium", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_deletes_a_folder_without_eligible_chats_and_validates_input()
    {
        var h = H(("Folders__Mode", "on"));
        h.Telegram.NotLinkable.UnionWith(Ids(2));
        var root = Json(await h.ShareFolder.RunAsync("Uz", Ids(2), Ct));
        Assert.Equal("nothing_shared", root.GetProperty("status").GetString());
        Assert.Single(h.Telegram.DeletedFolders);
        Assert.Empty(h.Telegram.FolderLinks);
        Assert.StartsWith("bad argument:", (await h.ShareFolder.RunAsync("A name that is too long", Ids(2), Ct)).Text, StringComparison.Ordinal);
        Assert.StartsWith("bad argument:", (await h.ShareFolder.RunAsync("Uz", [], Ct)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_draft_then_approve_creates_the_links()
    {
        var h = H(("Folders__Mode", "draft"));
        var root = Json(await h.ShareFolder.RunAsync("Uz", [.. Ids(3), 111], Ct));
        Assert.Equal("draft", root.GetProperty("status").GetString());
        var id = root.GetProperty("draft_id").GetString()!;
        Assert.Empty(h.Telegram.CreatedFolders);
        Assert.Single(root.GetProperty("excluded").EnumerateArray());
        Assert.Equal(Draft.ShareFolderKind, h.DraftStore.Drafts[id].Kind);
        Assert.Equal(3, h.DraftStore.Drafts[id].ShareFolder!.ChatIds.Count);

        var approved = Json(await h.DraftCommands.ApproveAsync(id, Ct));
        Assert.Equal("shared", approved.GetProperty("status").GetString());
        Assert.Single(h.Telegram.FolderLinks);
        Assert.Empty(h.DraftStore.Drafts);
        Assert.Equal("approve", h.LastAudit.Tool);
        NoSlugInAudit(h);
    }

    [Fact]
    public async Task Join_preview_is_read_only_and_works_in_mode_off()
    {
        var h = H();
        var root = Json(await h.JoinFolder.RunAsync(Link, true, Ct));
        Assert.Equal("preview", root.GetProperty("status").GetString());
        Assert.Equal("Uz groups", root.GetProperty("folder").GetString());
        Assert.Equal(3, root.GetProperty("would_join").GetArrayLength());
        Assert.Equal(1, root.GetProperty("already_in").GetInt32());
        Assert.Empty(h.Telegram.JoinedFolders);
        NoSlugInAudit(h);
    }

    [Fact]
    public async Task Join_on_joins_all_chats_except_denied_ones()
    {
        var h = H(("Folders__Mode", "on"), ("Deny__0__Id", "-1000000000002"));
        var root = Json(await h.JoinFolder.RunAsync(Link + "?utm=x", false, Ct));
        Assert.Equal("joined", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("excluded_by_policy").GetInt32());
        var joined = Assert.Single(h.Telegram.JoinedFolders);
        Assert.Equal("AbCdEfGh12", joined.Slug);
        Assert.Equal([-1000000000001, -1000000000003], joined.Chats);
        Assert.Contains("addlist#", h.LastAudit.Reason, StringComparison.Ordinal);
        NoSlugInAudit(h);
    }

    [Fact]
    public async Task Join_draft_approve_reject_and_limits()
    {
        var h = H(("Folders__Mode", "draft"));
        var root = Json(await h.JoinFolder.RunAsync(Link, false, Ct));
        var id = root.GetProperty("draft_id").GetString()!;
        Assert.Contains("about 2 shared folders", root.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(h.Telegram.JoinedFolders);
        var draft = h.DraftStore.Drafts[id];
        Assert.Equal(new PendingJoinFolder("AbCdEfGh12", "Uz groups", 3), draft.JoinFolder);
        Assert.Equal("joined", Json(await h.DraftCommands.ApproveAsync(id, Ct)).GetProperty("status").GetString());
        Assert.Single(h.Telegram.JoinedFolders);

        var second = Json(await h.JoinFolder.RunAsync(Link, false, Ct)).GetProperty("draft_id").GetString()!;
        Assert.True(await h.DraftCommands.RejectAsync(second, Ct));
        Assert.Single(h.Telegram.JoinedFolders);
        NoSlugInAudit(h);

        var limited = H(("Folders__Mode", "draft"), ("Limits__JoinsPerHour", "1"));
        Assert.False((await limited.JoinFolder.RunAsync(Link, false, Ct)).IsError);
        Assert.Contains("limits.joinsPerHour=1", (await limited.JoinFolder.RunAsync(Link, false, Ct)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Join_errors_never_echo_the_link()
    {
        var h = H(("Folders__Mode", "on"));
        var bad = await h.JoinFolder.RunAsync("https://t.me/addlist/Unknown123", false, Ct);
        Assert.True(bad.IsError);
        Assert.Contains("invalid or expired", bad.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown123", bad.Text, StringComparison.Ordinal);
        var malformed = await h.JoinFolder.RunAsync("https://t.me/+SecretHash99", false, Ct);
        Assert.StartsWith("bad argument:", malformed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretHash99", malformed.Text, StringComparison.Ordinal);
        h.Telegram.AddFolderFailure = new TelegramCallException(400, "CHATLISTS_TOO_MUCH");
        var limit = await h.JoinFolder.RunAsync(Link, false, Ct);
        Assert.Contains("about 2 shared folders", limit.Text, StringComparison.Ordinal);
        foreach (var e in h.Audit.Entries) Assert.DoesNotContain("Unknown123", e.Reason, StringComparison.Ordinal);
        NoSlugInAudit(h);
    }
}
