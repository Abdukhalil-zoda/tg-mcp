using System.Collections;

namespace TgMcp.Domain.Tests;

/// <summary>Env parsing, validation and the global gates.</summary>
public class ConfigTests
{
    private static readonly ChatInfo Friend = PolicyTests.Friend;

    [Fact]
    public void Defaults_deny_everything()
    {
        var c = PolicyConfig.FromPairs();
        Assert.True(c.IsValid);
        Assert.False(c.Enabled);
        Assert.Empty(c.Switches);
        Assert.False(c.DefaultRead);
        Assert.Equal(WriteMode.Off, c.DefaultWrite);
        Assert.Empty(c.DefaultFeatures);
        Assert.Equal(ListChatsMode.AllowedOnly, c.ListChats);
        Assert.Equal(200, c.MaxMessagesPerCall);
        Assert.Equal(30, c.RatePerMinute);
        Assert.Equal(50, c.MaxMediaMb);
        Assert.Equal(7, c.MediaRetentionDays);
        var d = new PolicyEngine(c).Check(Friend, Need.Read);
        Assert.False(d.Allowed);
        Assert.Contains("\"enabled\"", d.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void From_environment_strips_prefix_and_nesting_and_ignores_other_vars()
    {
        IDictionary env = new Hashtable
        {
            ["TGMCP_Enabled"] = "true",
            ["TGMCP_Features__Read"] = "true",
            ["TGMCP_Chats__0__Id"] = "111",
            ["TGMCP_Chats__0__Read"] = "true",
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/x",
        };
        var c = PolicyConfig.FromEnvironment(env);
        Assert.True(c.IsValid, string.Join("; ", c.Errors));
        Assert.True(new PolicyEngine(c).Check(Friend, Need.Read).Allowed);
    }

    [Fact]
    public void Unknown_key_denies_all_even_with_valid_rules()
    {
        var c = PolicyConfig.FromPairs(("Enabled", "true"), ("Features__Read", "true"), ("Chats__0__Id", "111"),
            ("Chats__0__Read", "true"), ("Limits__MaxMesagesPerCall", "10"));
        Assert.False(c.IsValid);
        Assert.Contains(c.Errors, e => e.Contains("unknown key", StringComparison.Ordinal));
        Assert.False(new PolicyEngine(c).Check(Friend, Need.Read).Allowed);
    }

    [Fact]
    public void Keys_are_case_insensitive_but_duplicates_are_errors()
    {
        Assert.True(PolicyConfig.FromPairs(("ENABLED", "TRUE"), ("features__read", "True")).IsValid);
        var c = PolicyConfig.Parse([new("Enabled", "true"), new("ENABLED", "false")]);
        Assert.False(c.IsValid);
    }

    [Fact]
    public void Section_keys_with_null_values_are_ignored()
    {
        // IConfiguration.AsEnumerable() yields parent sections with null values.
        var c = PolicyConfig.Parse([new("Chats", null), new("Chats:0", null), new("Chats:0:Id", "5")]);
        Assert.True(c.IsValid, string.Join("; ", c.Errors));
        Assert.Single(c.Chats);
    }

    [Fact]
    public void Runtime_keys_are_accepted_and_never_hashed_or_reported()
    {
        var a = PolicyConfig.FromPairs(("ApiId", "12345"), ("ApiHash", "secret-hash-value"), ("DatabaseKey", "k"));
        var b = PolicyConfig.FromPairs(("ApiId", "999"), ("ApiHash", "other"));
        Assert.True(a.IsValid);
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.DoesNotContain("secret-hash-value", string.Join("\n", ConfigReport.Describe(a)), StringComparison.Ordinal);
        var bad = PolicyConfig.FromPairs(("ApiHash", "secret-hash-value"), ("Bogus", "1"));
        Assert.DoesNotContain(bad.Errors, e => e.Contains("secret-hash-value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void Chat_rule_needs_exactly_one_selector(int selectors)
    {
        var kv = new List<(string, string)> { ("Chats__0__Read", "true") };
        var all = new[] { ("Chats__0__Id", "1"), ("Chats__0__Username", "abc"), ("Chats__0__Title", "T") };
        kv.AddRange(all.Take(selectors));
        var c = PolicyConfig.FromPairs([.. kv]);
        Assert.False(c.IsValid);
        Assert.Contains(c.Errors, e => e.Contains("exactly one selector", StringComparison.Ordinal));
    }

    [Fact]
    public void Deny_rule_needs_exactly_one_selector()
    {
        Assert.False(PolicyConfig.FromPairs(("Deny__0__Id", "1"), ("Deny__0__Type", "bot")).IsValid);
        Assert.True(PolicyConfig.FromPairs(("Deny__0__Folder", "Banks")).IsValid);
    }

    [Theory]
    [InlineData("Defaults__Write", "maybe")]
    [InlineData("Limits__ListChats", "everything")]
    [InlineData("Deny__0__Type", "supergroup")]
    [InlineData("Limits__MaxMediaMb", "-1")]
    [InlineData("Limits__MaxMessagesPerCall", "1.5")]
    public void Bad_enum_or_number_is_an_error(string key, string value) =>
        Assert.False(PolicyConfig.FromPairs((key, value)).IsValid);

    [Theory]
    [InlineData("0", true, 0)]
    [InlineData("30", true, 30)]
    [InlineData("-1", false, 7)]
    [InlineData("week", false, 7)]
    public void Media_retention_days(string value, bool valid, int expected)
    {
        var c = PolicyConfig.FromPairs(("Limits__MediaRetentionDays", value));
        Assert.Equal(valid, c.IsValid);
        Assert.Equal(expected, c.MediaRetentionDays);
    }

    [Fact]
    public void Env_entries_override_file_entries_and_their_children()
    {
        var c = PolicyConfig.Parse(
        [
            new ConfigEntry("enabled", "true", ConfigOrigin.File),
            new ConfigEntry("chats:0:id", "1", ConfigOrigin.File),
            new ConfigEntry("chats:0:features:0", "search", ConfigOrigin.File),
            new ConfigEntry("chats:0:features:1", "pin", ConfigOrigin.File),
            new ConfigEntry("Chats:0:Features", "react", ConfigOrigin.Env),
            new ConfigEntry("Enabled", "false", ConfigOrigin.Env),
        ]);
        Assert.True(c.IsValid, string.Join("; ", c.Errors));
        Assert.False(c.Enabled);
        Assert.Equal(new HashSet<Feature> { Feature.React }, c.Chats[0].Features);
        Assert.Equal("env", c.SourceOf("enabled"));
        Assert.Equal("file", c.SourceOf("chats:0:id"));
    }

    [Fact]
    public void Errors_use_json_paths_for_file_keys_and_variable_names_for_env_keys()
    {
        var c = PolicyConfig.Parse(
        [
            new ConfigEntry("chats:1:id", "1", ConfigOrigin.File),
            new ConfigEntry("chats:1:reed", "true", ConfigOrigin.File),
            new ConfigEntry("Deny:3:Kind", "bot", ConfigOrigin.Env),
        ]);
        Assert.Contains("chats[1].reed: unknown key", c.Errors);
        Assert.Contains("TGMCP_Deny__3__Kind: unknown key", c.Errors);
        Assert.Contains(c.Errors, e => e.StartsWith("TGMCP_Deny__3: needs exactly one selector", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_error_makes_config_invalid()
    {
        var c = PolicyConfig.Parse([new ConfigEntry("Enabled", "true", ConfigOrigin.Env)], "config file /x.json: not found");
        Assert.False(c.IsValid);
        Assert.Contains("config file /x.json: not found", c.Errors);
    }

    [Fact]
    public void Notify_without_read_is_a_config_error()
    {
        var rule = PolicyConfig.FromPairs(("Chats__0__Id", "1"), ("Chats__0__Notify", "true"));
        Assert.Contains(rule.Errors, e => e.StartsWith("TGMCP_Chats__0: \"notify\": true needs \"read\": true", StringComparison.Ordinal));
        var dflt = PolicyConfig.FromPairs(("Defaults__Notify", "true"));
        Assert.Contains(dflt.Errors, e => e.Contains("\"notify\": true needs \"read\": true", StringComparison.Ordinal));
        Assert.True(PolicyConfig.FromPairs(("Defaults__Read", "true"), ("Defaults__Notify", "true")).IsValid);
        Assert.True(PolicyConfig.FromPairs(("Defaults__Notify", "true"), ("Chats__0__Id", "1"), ("Chats__0__Read", "true"), ("Defaults__Read", "true")).IsValid);
        // a rule may opt out of a defaults notify without read
        Assert.True(PolicyConfig.FromPairs(("Defaults__Read", "true"), ("Defaults__Notify", "true"), ("Chats__0__Id", "1"), ("Chats__0__Read", "false"), ("Chats__0__Notify", "false")).IsValid);
    }

    [Fact]
    public void Events_section_defaults_and_validation()
    {
        var c = PolicyConfig.FromPairs();
        Assert.False(c.EventsEnabled);
        Assert.False(c.EventsIncludeOutgoing);
        Assert.Equal(30, c.EventsMaxPerMinute);
        Assert.Equal(4000, c.EventsMaxTextChars);
        Assert.Equal(0, c.EventsCatchUpMinutes);
        Assert.False(PolicyConfig.FromPairs(("Events__MaxPerMinute", "0")).IsValid);
        Assert.False(PolicyConfig.FromPairs(("Events__Bogus", "1")).IsValid);
        Assert.True(PolicyConfig.FromPairs(("Events__CatchUpMinutes", "0")).IsValid);
        var report = string.Join("\n", ConfigReport.Describe(PolicyConfig.FromPairs(("Events__Enabled", "true"), ("Chats__0__Id", "1"), ("Chats__0__Read", "true"), ("Chats__0__Notify", "true"))));
        Assert.Contains("events.enabled = true [env]", report, StringComparison.Ordinal);
        Assert.Contains("events: ON -> notify from chats[0]", report, StringComparison.Ordinal);
        Assert.Contains("notify=true", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Master_switch_off_denies_even_with_rules()
    {
        var p = new PolicyEngine(PolicyConfig.FromPairs(("Enabled", "false"), ("Features__Read", "true"),
            ("Chats__0__Id", "111"), ("Chats__0__Read", "true")));
        Assert.False(p.Check(Friend, Need.Read).Allowed);
        Assert.False(p.CheckGlobal().Allowed);
    }

    [Fact]
    public void Global_switch_off_denies_even_if_chat_allows()
    {
        var p = new PolicyEngine(PolicyConfig.FromPairs(("Enabled", "true"),
            ("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Features", "search")));
        var d = p.Check(Friend, Need.Read);
        Assert.False(d.Allowed);
        Assert.Contains("\"features.read\" is off", d.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Feature_needs_both_global_and_chat()
    {
        var chatOnly = new PolicyEngine(PolicyConfig.FromPairs(("Enabled", "true"), ("Features__Read", "true"),
            ("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Features", "search")));
        Assert.False(chatOnly.Check(Friend, Need.Read, Need.Search).Allowed);

        var globalOnly = new PolicyEngine(PolicyConfig.FromPairs(("Enabled", "true"), ("Features__Read", "true"),
            ("Features__Search", "true"), ("Chats__0__Id", "111"), ("Chats__0__Read", "true")));
        Assert.False(globalOnly.Check(Friend, Need.Read, Need.Search).Allowed);

        var both = new PolicyEngine(PolicyConfig.FromPairs(("Enabled", "true"), ("Features__Read", "true"),
            ("Features__Search", "true"), ("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Features", "search")));
        Assert.True(both.Check(Friend, Need.Read, Need.Search).Allowed);
    }

    [Fact]
    public void Send_switch_gates_write()
    {
        var p = new PolicyEngine(PolicyConfig.FromPairs(("Enabled", "true"), ("Chats__0__Id", "111"), ("Chats__0__Write", "on")));
        Assert.Contains("features.send", p.Check(Friend, Need.Write).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Deny_wins_over_first_match_and_defaults()
    {
        var p = PolicyTests.On(("Defaults__Read", "true"), ("Chats__0__Id", "111"), ("Chats__0__Read", "true"),
            ("Deny__5__Username", "friend"));
        var d = p.Check(Friend, Need.Read);
        Assert.False(d.Allowed);
        Assert.Equal("deny[5] username=\"friend\"", d.Rule);
    }

    [Fact]
    public void Folder_deny_fails_closed_until_resolved()
    {
        var p = PolicyTests.On(("Defaults__Read", "true"), ("Deny__0__Folder", "Finance"));
        Assert.False(p.FoldersResolved);
        Assert.False(p.Check(Friend, Need.Read).Allowed);

        p.SetFolders(new Dictionary<string, IReadOnlySet<long>> { ["Finance"] = new HashSet<long> { 333 } });
        Assert.True(p.FoldersResolved);
        Assert.True(p.Check(Friend, Need.Read).Allowed);
        Assert.False(p.Check(PolicyTests.Bank, Need.Read).Allowed);
    }

    [Fact]
    public void Folder_deny_with_unknown_folder_denies_all()
    {
        var p = PolicyTests.On(("Defaults__Read", "true"), ("Deny__0__Folder", "Finance"));
        p.SetFolders(new Dictionary<string, IReadOnlySet<long>> { ["Work"] = new HashSet<long> { 1 } });
        var d = p.Check(Friend, Need.Read);
        Assert.False(d.Allowed);
        Assert.Contains("folder", d.Reason, StringComparison.Ordinal);
        p.SetFolderError("network");
        Assert.False(p.Check(Friend, Need.Read).Allowed);
    }

    [Fact]
    public void Folders_irrelevant_without_folder_deny()
    {
        var p = PolicyTests.On(("Defaults__Read", "true"));
        Assert.True(p.FoldersResolved);
        Assert.True(p.Check(Friend, Need.Read).Allowed);
    }

    [Fact]
    public void Report_lists_rules_and_inactive_features()
    {
        var c = PolicyConfig.FromPairs(("Enabled", "true"), ("Features__Read", "true"),
            ("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Features", "search"), ("Deny__0__Type", "bot"));
        var text = string.Join("\n", ConfigReport.Describe(c));
        Assert.Contains("chats[0] id=111", text, StringComparison.Ordinal);
        Assert.Contains("search (inactive: features.search is off)", text, StringComparison.Ordinal);
        Assert.Contains("deny[0] type=bot", text, StringComparison.Ordinal);
        Assert.Contains(c.Sha256, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_for_invalid_config_shows_errors()
    {
        var c = PolicyConfig.FromPairs(("Chats__0__Read", "true"));
        var text = string.Join("\n", ConfigReport.Describe(c));
        Assert.Contains("INVALID", text, StringComparison.Ordinal);
        Assert.Contains("exactly one selector", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalized_form_ignores_key_case_and_order()
    {
        var a = PolicyConfig.FromPairs(("Enabled", "true"), ("Chats__0__Id", "1"), ("Chats__0__Features", "search,pin"));
        var b = PolicyConfig.FromPairs(("chats__0__features", "pin, search"), ("chats__0__id", "1"), ("enabled", "TRUE"));
        Assert.Equal(a.Sha256, b.Sha256);
    }
}
