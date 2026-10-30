using System.Collections;
using TgMcp.Domain;
using TgMcp.Server.Cli;
using TgMcp.Server.Hosting;

namespace TgMcp.Infrastructure.Tests;

public sealed class PolicyConfigLoaderTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string WriteFile(string json, string name = "tgmcp.json")
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllText(path, json);
        return path;
    }

    private static Hashtable Env(params (string, string)[] kv)
    {
        var env = new Hashtable();
        foreach (var (k, v) in kv) env[k] = v;
        return env;
    }

    private LoadedConfig Load(string json, params (string, string)[] env) =>
        PolicyConfigLoader.Load(Env([.. env, ("TGMCP_ConfigFile", WriteFile(json))]));

    private static readonly ChatInfo Work = new(-1001234567890, "Work", (string?)null, ChatKind.Group);

    private const string Basic = """
        {
          // line comment
          "enabled": true, /* block comment */
          "features": { "read": true, "search": true, },
          "chats": [
            { "id": -1001234567890, "name": "Work group", "read": true, "features": ["search"], },
          ],
          "limits": { "ratePerMinute": 5 },
        }
        """;

    [Fact]
    public void Parses_comments_and_trailing_commas()
    {
        var c = Load(Basic).Policy;
        Assert.True(c.IsValid, string.Join("; ", c.Errors));
        Assert.True(c.Enabled);
        Assert.Equal(5, c.RatePerMinute);
        var rule = Assert.Single(c.Chats);
        Assert.Equal("Work group", rule.Name);
        Assert.True(new PolicyEngine(c).Check(Work, Need.Read, Need.Search).Allowed);
    }

    [Fact]
    public void Environment_overrides_the_file()
    {
        var c = Load(Basic, ("TGMCP_Limits__RatePerMinute", "10"), ("TGMCP_Chats__0__Features", "")).Policy;
        Assert.True(c.IsValid, string.Join("; ", c.Errors));
        Assert.Equal(10, c.RatePerMinute);
        Assert.Equal("env", c.SourceOf("limits:ratePerMinute"));
        Assert.Equal("file", c.SourceOf("enabled"));
        Assert.Equal("default", c.SourceOf("limits:maxMediaMb"));
        Assert.Equal("file+env", c.SourceOf("chats:0"));
        Assert.Empty(c.Chats[0].Features!); // env "" replaced the file's ["search"]
        Assert.False(new PolicyEngine(c).Check(Work, Need.Read, Need.Search).Allowed);
    }

    [Fact]
    public void Environment_can_switch_everything_off()
    {
        var c = Load(Basic, ("TGMCP_Enabled", "false")).Policy;
        Assert.False(new PolicyEngine(c).Check(Work, Need.Read).Allowed);
    }

    [Theory]
    [InlineData("apiHash", "\"0123456789abcdef\"")]
    [InlineData("ApiId", "12345")]
    [InlineData("databaseKey", "\"k\"")]
    public void Secrets_in_the_file_are_refused(string key, string value)
    {
        var c = Load($$"""{ "enabled": true, "{{key}}": {{value}} }""").Policy;
        Assert.False(c.IsValid);
        Assert.Contains(c.Errors, e => e.Contains("secrets are not allowed in the config file", StringComparison.Ordinal));
        Assert.DoesNotContain(c.Errors, e => e.Contains("0123456789abcdef", StringComparison.Ordinal));
        Assert.DoesNotContain("0123456789abcdef", string.Join("\n", ConfigReport.Describe(c)), StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_settings_in_the_file_are_refused()
    {
        var c = Load("""{ "dataDir": "/tmp" }""").Policy;
        Assert.Contains(c.Errors, e => e.StartsWith("dataDir: only allowed as the environment variable TGMCP_DataDir", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_keys_are_reported_with_their_json_path()
    {
        var c = Load("""
            { "chats": [ { "id": 1 }, { "id": 2 }, { "id": 3, "reed": true } ], "limits": { "maxMesages": 1 } }
            """).Policy;
        Assert.False(c.IsValid);
        Assert.Contains("chats[2].reed: unknown key", c.Errors);
        Assert.Contains("limits.maxMesages: unknown key", c.Errors);
    }

    [Fact]
    public void Selector_errors_name_the_rule_by_json_path()
    {
        var c = Load("""{ "chats": [ { "name": "only a label", "read": true } ] }""").Policy;
        Assert.Contains(c.Errors, e => e.StartsWith("chats[0]: needs exactly one selector of id|username|title", StringComparison.Ordinal));
    }

    [Fact]
    public void Env_errors_name_the_variable()
    {
        var c = Load("{}", ("TGMCP_Chats__4__Reed", "true"), ("TGMCP_Chats__4__Id", "5")).Policy;
        Assert.Contains("TGMCP_Chats__4__Reed: unknown key", c.Errors);
    }

    [Fact]
    public void Features_accept_arrays_and_comma_strings_alike()
    {
        var arr = Load("""{ "chats": [ { "id": 1, "features": ["search", "media_download"] } ] }""").Policy;
        var str = Load("""{ "chats": [ { "id": 1, "features": "search, media_download" } ] }""").Policy;
        var env = PolicyConfig.FromPairs(("Chats__0__Id", "1"), ("Chats__0__Features", "search,media_download"));
        Assert.Equal(arr.Chats[0].Features, str.Chats[0].Features);
        Assert.Equal(arr.Chats[0].Features, env.Chats[0].Features);
        Assert.Equal(arr.Sha256, env.Sha256);
    }

    [Fact]
    public void Empty_feature_array_overrides_defaults_and_empty_topics_mean_all()
    {
        var c = Load("""
            { "enabled": true, "features": { "read": true, "search": true },
              "defaults": { "features": ["search"] },
              "chats": [ { "id": -1001234567890, "read": true, "features": [], "topics": [] } ] }
            """).Policy;
        Assert.True(c.IsValid, string.Join("; ", c.Errors));
        var d = new PolicyEngine(c).Check(Work, Need.Read);
        Assert.Empty(d.FeatureSet);
        Assert.Null(d.Topics);
        Assert.True(d.TopicOk(99));
    }

    [Theory]
    [InlineData("""{ "enabled": true, "deny": [] }""")]
    [InlineData("""{ "enabled": true, "chats": [] }""")]
    [InlineData("""{ "enabled": true, "chats": [], "deny": [], "defaults": { "features": [] } }""")]
    [InlineData("""{ "enabled": true, "chats": [ { "id": 1, "features": [], "topics": [] } ] }""")]
    public void Empty_arrays_mean_no_entries(string json)
    {
        var c = Load(json).Policy;
        Assert.True(c.IsValid, string.Join("; ", c.Errors));
        Assert.Empty(c.Denies);
        Assert.Empty(c.DefaultFeatures);
        Assert.All(c.Chats, r => { Assert.Empty(r.Features!); Assert.Null(r.Topics); });
    }

    [Fact]
    public void Empty_deny_list_denies_nothing_and_empty_chats_use_defaults()
    {
        var c = Load("""{ "enabled": true, "features": { "read": true }, "defaults": { "read": true }, "chats": [], "deny": [] }""").Policy;
        Assert.True(new PolicyEngine(c).Check(Work, Need.Read).Allowed);
    }

    [Fact]
    public void Name_and_note_are_never_used_for_matching()
    {
        var c = Load("""
            { "enabled": true, "features": { "read": true },
              "chats": [ { "id": 5, "name": "Work", "read": true }, { "username": "other", "note": "Work", "read": true } ] }
            """).Policy;
        Assert.True(c.IsValid, string.Join("; ", c.Errors));
        Assert.Equal("Work", c.Chats[1].Name);
        Assert.False(new PolicyEngine(c).Check(Work, Need.Read).Allowed); // title "Work" matches no rule
        Assert.Equal(PolicyConfig.FromPairs(("Enabled", "true"), ("Features__Read", "true"), ("Chats__0__Id", "5"), ("Chats__0__Read", "true"),
            ("Chats__1__Username", "other"), ("Chats__1__Read", "true")).Sha256, c.Sha256);
    }

    [Fact]
    public void Explicit_missing_file_denies_everything()
    {
        var loaded = PolicyConfigLoader.Load(Env(("TGMCP_ConfigFile", Path.Combine(_dir.Path, "nope.json")), ("TGMCP_Enabled", "true")));
        Assert.False(loaded.FileFound);
        Assert.False(loaded.Policy.IsValid);
        Assert.Contains(loaded.Policy.Errors, e => e.EndsWith("not found", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_default_file_means_environment_only()
    {
        var loaded = PolicyConfigLoader.Load(Env(("TGMCP_Enabled", "true"), ("TGMCP_Features__Read", "true"),
            ("TGMCP_Chats__0__Id", "-1001234567890"), ("TGMCP_Chats__0__Read", "true")));
        Assert.Equal(PolicyConfigLoader.DefaultPath, loaded.FilePath);
        Assert.False(loaded.Explicit);
        Assert.True(loaded.Policy.IsValid, string.Join("; ", loaded.Policy.Errors));
        Assert.True(new PolicyEngine(loaded.Policy).Check(Work, Need.Read).Allowed);
    }

    [Fact]
    public void Malformed_json_denies_everything()
    {
        var c = Load("""{ "enabled": true, """ + "\n" + """ "chats": [ { "id": 1 } """).Policy;
        Assert.False(c.IsValid);
        Assert.Contains(c.Errors, e => e.Contains("cannot be read or parsed", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_keys_in_the_file_deny_everything()
    {
        var c = Load("""{ "enabled": true, "Enabled": false }""").Policy;
        Assert.False(c.IsValid);
    }

    [Fact]
    public void Media_section_is_read_and_validated()
    {
        var ok = Load("""{ "media": { "dir": "/media", "hostDir": "/var/lib/tgmcp/media" } }""").Policy;
        Assert.Equal("/media", ok.MediaDir);
        Assert.Equal("/var/lib/tgmcp/media", ok.MediaHostDir);
        Assert.False(Load("""{ "media": { "dir": "media" } }""").Policy.IsValid);
        Assert.False(Load("""{ "media": { "folder": "/m" } }""").Policy.IsValid);
    }

    [Fact]
    public void Example_template_is_valid_and_keeps_its_safety_rules()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tgmcp.example.jsonc"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var loaded = PolicyConfigLoader.Load(Env(), Path.Combine(dir.FullName, "tgmcp.example.jsonc"));
        var c = loaded.Policy;
        Assert.True(c.IsValid, string.Join("; ", c.Errors));
        var p = new PolicyEngine(c);
        p.SetFolders(new Dictionary<string, IReadOnlySet<long>> { ["Personal"] = new HashSet<long> { 42 } });
        Assert.False(p.Check(new ChatInfo(333, "Bank", "bank_bot", ChatKind.Bot), Need.Read).Allowed);
        Assert.False(p.Check(new ChatInfo(777000, "Telegram", (string?)null, ChatKind.Private), Need.Read).Allowed);
        Assert.False(p.Check(new ChatInfo(42, "Family", (string?)null, ChatKind.Group), Need.Read).Allowed);
        Assert.True(p.Check(Work, Need.Read).Allowed);
        Assert.False(p.Check(Work, Need.Read, Need.Search).Allowed);
        Assert.Contains(c.Chats, r => r.Name == "Work group");
    }

    [Fact]
    public void Config_check_prints_sources_and_rule_names_for_a_given_file()
    {
        var path = WriteFile(Basic, "other.json");
        using var output = new StringWriter();
        var code = ConfigCheckCommand.Run(Env(("TGMCP_Limits__MaxMediaMb", "20")), path, output);
        var text = output.ToString();
        Assert.Equal(0, code);
        Assert.Contains($"config file: {path} - found", text, StringComparison.Ordinal);
        Assert.Contains("enabled = true [file]", text, StringComparison.Ordinal);
        Assert.Contains("limits.maxMediaMb = 20 [env]", text, StringComparison.Ordinal);
        Assert.Contains("limits.maxMessagesPerCall = 200 [default]", text, StringComparison.Ordinal);
        Assert.Contains("chats[0] id=-1001234567890 \"Work group\" [file]", text, StringComparison.Ordinal);
        Assert.Contains("result: OK", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_check_fails_for_an_invalid_file()
    {
        using var output = new StringWriter();
        var code = ConfigCheckCommand.Run(Env(), WriteFile("""{ "chats": [ { "id": 1, "reed": true } ] }"""), output);
        Assert.Equal(1, code);
        Assert.Contains("chats[0].reed: unknown key", output.ToString(), StringComparison.Ordinal);
    }
}
