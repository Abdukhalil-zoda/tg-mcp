namespace TgMcp.Domain.Tests;

/// <summary>addMember, folders, searchPublic, findChats and the new pace limits.</summary>
public class MemberAndFolderPolicyTests
{
    private static readonly ChatInfo Group = new(-1001234567890, "Example Group", "example_group", ChatKind.Group);
    private static readonly ChatInfo Other = new(-1009999999999, "Other", (string?)null, ChatKind.Group);

    private static PolicyEngine P(params (string, string)[] kv) =>
        new(PolicyConfig.FromPairs([("Enabled", "true"), ("Features__AddMember", "true"), ("Features__Folders", "true"), .. kv]));

    [Fact]
    public void Defaults_are_off_and_small()
    {
        var c = PolicyConfig.FromPairs();
        Assert.True(c.IsValid);
        Assert.Equal(WriteMode.Off, c.AddMemberMode);
        Assert.Empty(c.AddMemberChats);
        Assert.Equal(WriteMode.Off, c.FoldersMode);
        Assert.Equal(5, c.AddsPerHour);
        Assert.Equal(20, c.AddsPerDay);
        Assert.Equal(20, c.JoinsPerDay);
        Assert.Equal(20, c.PublicSearchesPerHour);
        foreach (var s in new[] { GlobalSwitch.SearchPublic, GlobalSwitch.AddMember, GlobalSwitch.FindChats, GlobalSwitch.Folders })
            Assert.DoesNotContain(s, c.Switches);
    }

    [Fact]
    public void Parses_switches_modes_chats_and_limits()
    {
        var c = PolicyConfig.FromPairs(("Features__SearchPublic", "true"), ("Features__FindChats", "true"), ("Features__AddMember", "true"),
            ("Features__Folders", "true"), ("AddMember__Mode", "on"), ("AddMember__Chats", "-1001234567890, @Example_Group"),
            ("Folders__Mode", "draft"), ("Limits__AddsPerHour", "2"), ("Limits__AddsPerDay", "7"), ("Limits__JoinsPerDay", "9"),
            ("Limits__PublicSearchesPerHour", "3"));
        Assert.True(c.IsValid, string.Join("; ", c.Errors));
        Assert.Contains(GlobalSwitch.SearchPublic, c.Switches);
        Assert.Contains(GlobalSwitch.FindChats, c.Switches);
        Assert.Equal(WriteMode.On, c.AddMemberMode);
        Assert.Equal([new ChatRef(-1001234567890, null), new ChatRef(0, "example_group")], c.AddMemberChats);
        Assert.Equal(WriteMode.Draft, c.FoldersMode);
        Assert.Equal((2, 7, 9, 3), (c.AddsPerHour, c.AddsPerDay, c.JoinsPerDay, c.PublicSearchesPerHour));
    }

    [Fact]
    public void File_array_of_chats_is_collapsed_like_env_list()
    {
        var file = PolicyConfig.Parse(
        [
            new ConfigEntry("addMember:mode", "on", ConfigOrigin.File),
            new ConfigEntry("addMember:chats:0", "-1001234567890", ConfigOrigin.File),
            new ConfigEntry("addMember:chats:1", "@example_group", ConfigOrigin.File),
        ]);
        Assert.True(file.IsValid, string.Join("; ", file.Errors));
        var env = PolicyConfig.FromPairs(("AddMember__Mode", "on"), ("AddMember__Chats", "-1001234567890,@example_group"));
        Assert.Equal(env.AddMemberChats, file.AddMemberChats);
        Assert.Equal(env.Sha256, file.Sha256);
        Assert.True(PolicyConfig.Parse([new ConfigEntry("addMember:chats", "", ConfigOrigin.File)]).IsValid); // "chats": []
    }

    [Theory]
    [InlineData("AddMember__Chats", "*", "\"*\" is not allowed")]
    [InlineData("AddMember__Chats", "0", "not a chat id or @username")]
    [InlineData("AddMember__Chats", "bad name!", "not a chat id or @username")]
    [InlineData("AddMember__Mode", "maybe", "must be one of off|draft|on")]
    [InlineData("AddMember__Allow", "x", "unknown key")]
    [InlineData("Folders__Mode", "yes", "must be one of off|draft|on")]
    [InlineData("Folders__Allow", "x", "unknown key")]
    [InlineData("Limits__AddsPerHour", "0", "expected an integer >= 1")]
    [InlineData("Limits__AddsPerDay", "-3", "expected an integer >= 1")]
    [InlineData("Limits__JoinsPerDay", "lots", "expected an integer >= 1")]
    [InlineData("Limits__PublicSearchesPerHour", "1.5", "expected an integer >= 1")]
    public void Bad_values_make_the_config_invalid(string key, string value, string error)
    {
        var c = PolicyConfig.FromPairs((key, value));
        Assert.False(c.IsValid);
        Assert.Contains(c.Errors, e => e.Contains(error, StringComparison.Ordinal));
    }

    [Fact]
    public void Add_member_off_draft_on()
    {
        Assert.Equal("addMember.mode is off", P().CheckAddMember(Group).Reason);
        Assert.True(P(("AddMember__Mode", "draft")).CheckAddMember(Other).Allowed); // draft needs no chats list
        Assert.Equal("addMember.chats is empty", P(("AddMember__Mode", "on")).CheckAddMember(Group).Reason);
        var on = P(("AddMember__Mode", "on"), ("AddMember__Chats", "@EXAMPLE_GROUP"));
        Assert.True(on.CheckAddMember(Group).Allowed);
        Assert.Equal("chat is not in addMember.chats", on.CheckAddMember(Other).Reason);
        Assert.True(P(("AddMember__Mode", "on"), ("AddMember__Chats", "-1009999999999")).CheckAddMember(Other).Allowed);
    }

    [Fact]
    public void Global_switches_gate_every_mode()
    {
        foreach (var mode in new[] { "off", "draft", "on" })
        {
            var p = new PolicyEngine(PolicyConfig.FromPairs(("Enabled", "true"), ("AddMember__Mode", mode), ("AddMember__Chats", "@example_group"), ("Folders__Mode", mode)));
            var add = p.CheckAddMember(Group);
            Assert.False(add.Allowed);
            Assert.Contains("\"features.addMember\" is off", add.Reason, StringComparison.Ordinal);
            Assert.NotNull(add.Global);
            var f = p.CheckFolders();
            Assert.False(f.Allowed);
            Assert.Contains("\"features.folders\" is off", f.Reason, StringComparison.Ordinal);
        }
        Assert.Contains("\"features.searchPublic\" is off", P().CheckSwitches(Need.SearchPublic).Reason, StringComparison.Ordinal);
        Assert.Contains("\"features.findChats\" is off", P().CheckSwitches(Need.FindChats).Reason, StringComparison.Ordinal);
        Assert.False(new PolicyEngine(PolicyConfig.FromPairs(("Features__AddMember", "true"), ("AddMember__Mode", "draft"))).CheckAddMember(Group).Allowed);
    }

    [Fact]
    public void Folders_mode()
    {
        Assert.Equal("folders.mode is off", P().CheckFolders().Reason);
        Assert.Equal(WriteMode.Draft, P(("Folders__Mode", "draft")).CheckFolders().Mode);
        Assert.True(P(("Folders__Mode", "on")).CheckFolders().Allowed);
    }

    [Fact]
    public void New_needs_are_not_per_chat_features()
    {
        foreach (var n in new[] { Need.SearchPublic, Need.AddMember, Need.FindChats, Need.Folders })
            Assert.Null(Names.AsFeature(n));
    }

    [Fact]
    public void Config_check_shows_the_new_keys_and_the_hash_covers_them()
    {
        var c = PolicyConfig.FromPairs(("Features__FindChats", "true"), ("AddMember__Mode", "draft"), ("AddMember__Chats", "@example_group,-100123"),
            ("Folders__Mode", "on"), ("Limits__AddsPerDay", "8"));
        var report = string.Join("\n", ConfigReport.Describe(c));
        Assert.Contains("addMember.mode = draft [env]", report, StringComparison.Ordinal);
        Assert.Contains("addMember.chats = [@example_group, -100123] [env]", report, StringComparison.Ordinal);
        Assert.Contains("folders.mode = on [env]", report, StringComparison.Ordinal);
        Assert.Contains("limits.addsPerHour = 5 [default]", report, StringComparison.Ordinal);
        Assert.Contains("limits.addsPerDay = 8 [env]", report, StringComparison.Ordinal);
        Assert.Contains("limits.joinsPerDay = 20 [default]", report, StringComparison.Ordinal);
        Assert.Contains("limits.publicSearchesPerHour = 20 [default]", report, StringComparison.Ordinal);
        Assert.Contains("features.findChats = true (lists your groups/channels by title regardless of per-chat read rules", report, StringComparison.Ordinal);
        Assert.Contains("features.searchPublic = false", report, StringComparison.Ordinal);
        Assert.Contains("features.folders = false", report, StringComparison.Ordinal);
        Assert.NotEqual(PolicyConfig.FromPairs(("Folders__Mode", "draft")).Sha256, PolicyConfig.FromPairs(("Folders__Mode", "on")).Sha256);
        Assert.NotEqual(PolicyConfig.FromPairs(("AddMember__Chats", "-1")).Sha256, PolicyConfig.FromPairs(("AddMember__Chats", "-2")).Sha256);
        Assert.NotEqual(PolicyConfig.FromPairs(("Limits__JoinsPerDay", "3")).Sha256, PolicyConfig.FromPairs().Sha256);
    }

    [Theory]
    [InlineData("-1001234567890", -1001234567890L, null)]
    [InlineData("@Example_Group", 0L, "example_group")]
    [InlineData(" example_group ", 0L, "example_group")]
    public void Chat_ref_parses(string input, long id, string? username)
    {
        Assert.True(ChatRef.TryParse(input, out var r));
        Assert.Equal(new ChatRef(id, username), r);
    }

    [Theory]
    [InlineData("https://t.me/addlist/AbCdEfGh12", "AbCdEfGh12")]
    [InlineData("t.me/addlist/AbCdEfGh12?x=1", "AbCdEfGh12")]
    [InlineData("HTTP://www.telegram.me/ADDLIST/AbCdEfGh12/", "AbCdEfGh12")]
    public void Folder_link_accepts(string input, string slug)
    {
        Assert.True(FolderLink.TryParse(input, out var l, out var error), error);
        Assert.Equal(slug, l!.Slug);
        Assert.Equal("https://t.me/addlist/" + slug, l.Canonical);
        Assert.Matches("^addlist#[0-9a-f]{8}$", l.AuditRef);
        Assert.DoesNotContain(slug, l.AuditRef, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AbCdEfGh12")]
    [InlineData("https://t.me/+AbCdEfGh12")]
    [InlineData("https://t.me/addlist/")]
    [InlineData("https://t.me/addlist/abc")]
    [InlineData("tg://addlist?slug=AbCdEfGh12")]
    [InlineData("https://evil.example/addlist/AbCdEfGh12")]
    public void Folder_link_rejects_without_echoing_the_input(string input)
    {
        Assert.False(FolderLink.TryParse(input, out _, out var error));
        Assert.DoesNotContain("AbCdEfGh12", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Join_chat_rejects_folder_links() =>
        Assert.False(JoinTarget.TryParse("https://t.me/addlist/AbCdEfGh12", out _, out _));
}
