namespace TgMcp.Domain.Tests;

/// <summary>Rule semantics (ported from the original draft's 26 policy tests, adapted to the env model).</summary>
public class PolicyTests
{
    internal static readonly ChatInfo Friend = new(111, "Friend", "Friend", ChatKind.Private);
    internal static readonly ChatInfo Team = new(-100222, "Dev Team", (string?)null, ChatKind.Group);
    internal static readonly ChatInfo Bank = new(333, "MyBank", "mybank_bot", ChatKind.Bot);
    internal static readonly ChatInfo News = new(-100444, "News", "news", ChatKind.Channel);
    internal static readonly ChatInfo[] All = [Friend, Team, Bank, News];

    /// <summary>Policy with the master switch and every global switch on, plus the given keys.</summary>
    internal static PolicyEngine On(params (string, string)[] kv)
    {
        var all = new List<(string, string)> { ("Enabled", "true") };
        all.AddRange(Enum.GetNames<GlobalSwitch>().Select(s => ($"Features__{s}", "true")));
        all.AddRange(kv);
        var cfg = PolicyConfig.FromPairs([.. all]);
        Assert.True(cfg.IsValid, string.Join("; ", cfg.Errors));
        return new PolicyEngine(cfg);
    }

    [Fact]
    public void Empty_policy_denies_everything()
    {
        var p = On();
        foreach (var chat in All)
            foreach (var need in new[] { Need.Read, Need.Write, Need.Search, Need.React, Need.Pin })
                Assert.False(p.Check(chat, need).Allowed, $"{chat.Id} {need}");
        Assert.Null(p.Listing(Friend));
    }

    [Fact]
    public void Unmatched_chat_uses_defaults()
    {
        var p = On(("Chats__0__Id", "111"), ("Chats__0__Read", "true"));
        var d = p.Check(Team, Need.Read);
        Assert.False(d.Allowed);
        Assert.Equal("defaults", d.Rule);
    }

    [Fact]
    public void Broken_policy_denies()
    {
        var p = new PolicyEngine(PolicyConfig.Invalid("missing"));
        Assert.False(p.Check(Friend).Allowed);
        Assert.Contains("config invalid", p.Check(Friend).Reason, StringComparison.Ordinal);
        Assert.Null(p.Listing(Friend));
    }

    [Theory]
    [InlineData("Chats__0__Read=true")]                         // no selector
    [InlineData("Chats__0__Id=1", "Chats__0__Username=x")]      // two selectors
    [InlineData("Chats__0__Id=1", "Chats__0__Write=yes")]       // bad write mode
    [InlineData("Chats__0__Id=1", "Chats__0__Features=nuke")]   // unknown feature
    [InlineData("Chats__0__Id=abc")]                            // id not integer
    [InlineData("Chats__0__Type=bot")]                          // type is deny-only
    [InlineData("Deny__0__Type=bank")]                          // unknown type
    [InlineData("Deny__0__Title=MyBank")]                       // title is chats-only
    [InlineData("Defaults__Read=yes")]                          // not bool
    [InlineData("Limits__ListChats=all")]                       // bad enum
    [InlineData("Limits__RatePerMinute=0")]                     // must be >= 1
    [InlineData("Oops=1")]                                      // unknown section
    [InlineData("Chats__0__Id=1", "Chats__0__Reed=true")]       // typo key
    [InlineData("Chats__0__Id=1", "Chats__0__SinceDays=0")]     // must be >= 1
    [InlineData("Chats__x__Id=1")]                              // index not numeric
    [InlineData("Chats__0__Id=1", "Chats__0__Topics=a,2")]      // topic not numeric
    [InlineData("Features__Everything=true")]                   // unknown switch
    [InlineData("Enabled=1")]                                   // bool must be true/false
    public void Invalid_configs_are_rejected(params string[] kv)
    {
        var cfg = PolicyConfig.FromPairs([.. kv.Select(Split), ("Enabled", "true"), ("Features__Read", "true")]);
        Assert.False(cfg.IsValid);
        Assert.NotEmpty(cfg.Errors);
        var p = new PolicyEngine(cfg);
        Assert.All(All, c => Assert.False(p.Check(c, Need.Read).Allowed));
    }

    internal static (string, string) Split(string s)
    {
        var i = s.IndexOf('=', StringComparison.Ordinal);
        return (s[..i], s[(i + 1)..]);
    }

    [Fact]
    public void Sha256_is_stable_and_sensitive()
    {
        var a = PolicyConfig.FromPairs();
        Assert.Equal(64, a.Sha256.Length);
        Assert.Equal(a.Sha256, PolicyConfig.FromPairs().Sha256);
        Assert.NotEqual(a.Sha256, PolicyConfig.FromPairs(("Limits__RatePerMinute", "10")).Sha256);
    }

    [Fact]
    public void Match_by_id()
    {
        var p = On(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"));
        Assert.True(p.Check(Team, Need.Read).Allowed);
        Assert.False(p.Check(Friend, Need.Read).Allowed);
    }

    [Fact]
    public void Match_by_username_ignores_case_and_at()
    {
        var p = On(("Chats__0__Username", "@FRIEND"), ("Chats__0__Read", "true"));
        Assert.True(p.Check(Friend, Need.Read).Allowed);
        Assert.False(p.Check(Team, Need.Read).Allowed); // no username
    }

    [Fact]
    public void Match_by_title_is_exact()
    {
        var p = On(("Chats__0__Title", "Dev Team"), ("Chats__0__Read", "true"));
        Assert.True(p.Check(Team, Need.Read).Allowed);
        Assert.False(p.Check(new ChatInfo(9, "Dev Team ", (string?)null, ChatKind.Group), Need.Read).Allowed);
        Assert.False(p.Check(new ChatInfo(9, "dev team", (string?)null, ChatKind.Group), Need.Read).Allowed);
    }

    [Fact]
    public void First_matching_rule_wins()
    {
        var p = On(("Chats__0__Id", "111"), ("Chats__0__Read", "false"),
                   ("Chats__1__Username", "friend"), ("Chats__1__Read", "true"));
        var d = p.Check(Friend, Need.Read);
        Assert.False(d.Allowed);
        Assert.StartsWith("chats[0]", d.Rule, StringComparison.Ordinal);
    }

    [Fact]
    public void Rules_are_ordered_numerically_not_lexically()
    {
        var p = On(("Chats__10__Id", "111"), ("Chats__10__Read", "false"),
                   ("Chats__2__Id", "111"), ("Chats__2__Read", "true"));
        Assert.True(p.Check(Friend, Need.Read).Allowed);
    }

    [Fact]
    public void Deny_by_type_beats_allow()
    {
        var p = On(("Defaults__Read", "true"), ("Chats__0__Id", "333"), ("Chats__0__Read", "true"),
                   ("Chats__0__Write", "on"), ("Deny__0__Type", "bot"));
        var d = p.Check(Bank, Need.Read);
        Assert.False(d.Allowed);
        Assert.StartsWith("deny[0]", d.Rule, StringComparison.Ordinal);
        Assert.True(p.Check(Friend, Need.Read).Allowed);
    }

    [Theory]
    [InlineData("Deny__0__Username", "MyBank_Bot")]
    [InlineData("Deny__0__Id", "333")]
    public void Deny_by_username_or_id(string key, string value)
    {
        var p = On(("Defaults__Read", "true"), (key, value));
        Assert.False(p.Check(Bank, Need.Read).Allowed);
        Assert.True(p.Check(Friend, Need.Read).Allowed);
    }

    [Fact]
    public void Denied_chat_is_hidden_from_names_all()
    {
        var p = On(("Limits__ListChats", "names_all"), ("Deny__0__Type", "bot"));
        Assert.Null(p.Listing(Bank));
        Assert.NotNull(p.Listing(Friend));
    }

    [Fact]
    public void Write_modes()
    {
        var p = On(("Chats__0__Id", "111"), ("Chats__0__Write", "draft"), ("Chats__1__Id", "-100222"), ("Chats__1__Write", "on"));
        Assert.True(p.Check(Friend, Need.Write).Allowed);
        Assert.Equal(WriteMode.Draft, p.Check(Friend, Need.Write).Write);
        Assert.False(p.Check(Friend, Need.WriteOn).Allowed);
        Assert.Equal(WriteMode.On, p.Check(Team, Need.Write).Write);
        Assert.True(p.Check(Team, Need.WriteOn).Allowed);
        Assert.False(p.Check(News, Need.Write).Allowed);
    }

    [Fact]
    public void Write_does_not_imply_read()
    {
        var p = On(("Chats__0__Id", "111"), ("Chats__0__Write", "on"));
        Assert.False(p.Check(Friend, Need.Read).Allowed);
    }

    [Fact]
    public void Defaults_write()
    {
        var p = On(("Defaults__Write", "draft"), ("Chats__0__Id", "111"), ("Chats__0__Write", "off"));
        Assert.False(p.Check(Friend, Need.Write).Allowed);
        Assert.Equal(WriteMode.Draft, p.Check(Team, Need.Write).Write);
    }

    [Fact]
    public void Feature_is_required()
    {
        var p = On(("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Features", "search"));
        Assert.True(p.Check(Friend, Need.Read, Need.Search).Allowed);
        var d = p.Check(Friend, Need.Read, Need.React);
        Assert.False(d.Allowed);
        Assert.Contains("feature react", d.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Rule_features_replace_defaults()
    {
        var p = On(("Defaults__Features", "react"), ("Chats__0__Id", "111"), ("Chats__0__Features", "pin"));
        Assert.False(p.Check(Friend, Need.React).Allowed);
        Assert.True(p.Check(Friend, Need.Pin).Allowed);
        Assert.True(p.Check(Team, Need.React).Allowed);
    }

    [Fact]
    public void Empty_feature_list_means_none()
    {
        var p = On(("Defaults__Features", "react"), ("Chats__0__Id", "111"), ("Chats__0__Features", ""));
        Assert.False(p.Check(Friend, Need.React).Allowed);
    }

    [Fact]
    public void All_needs_are_checked()
    {
        var p = On(("Chats__0__Id", "111"), ("Chats__0__Features", "search"));
        Assert.False(p.Check(Friend, Need.Search, Need.Read).Allowed);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void Since_days()
    {
        var d = On(("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__SinceDays", "7")).Check(Friend, Need.Read);
        Assert.True(d.DateOk(Now.AddDays(-6), Now));
        Assert.False(d.DateOk(Now.AddDays(-8), Now));
        Assert.Equal(Now.AddDays(-7), d.Cutoff(Now));
    }

    [Fact]
    public void No_since_limit()
    {
        var d = On(("Chats__0__Id", "111"), ("Chats__0__Read", "true")).Check(Friend, Need.Read);
        Assert.Null(d.Cutoff(Now));
        Assert.True(d.DateOk(DateTimeOffset.UnixEpoch, Now));
    }

    [Fact]
    public void Default_since_days_and_override()
    {
        var p = On(("Defaults__SinceDays", "3"), ("Chats__0__Id", "111"), ("Chats__0__Read", "true"),
                   ("Chats__1__Id", "-100222"), ("Chats__1__Read", "true"), ("Chats__1__SinceDays", "30"));
        Assert.Equal(3, p.Check(Friend, Need.Read).SinceDays);
        Assert.Equal(30, p.Check(Team, Need.Read).SinceDays);
    }

    [Fact]
    public void Topics_restrict_messages()
    {
        var d = On(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true"), ("Chats__0__Topics", "3, 7")).Check(Team, Need.Read);
        Assert.True(d.TopicOk(3));
        Assert.True(d.TopicOk(7));
        Assert.False(d.TopicOk(4));
        Assert.False(d.TopicOk(null)); // no topic info: fail closed
        var all = On(("Chats__0__Id", "-100222"), ("Chats__0__Read", "true")).Check(Team, Need.Read);
        Assert.True(all.TopicOk(null));
        Assert.True(all.TopicOk(99));
    }

    [Fact]
    public void Listing_allowed_only_shows_readable_chats()
    {
        var p = On(("Limits__ListChats", "allowed_only"), ("Chats__0__Id", "111"), ("Chats__0__Read", "true"),
                   ("Chats__1__Id", "-100222"), ("Chats__1__Write", "draft"));
        Assert.NotNull(p.Listing(Friend));
        Assert.Null(p.Listing(Team)); // write-only is not "Read"
        Assert.Null(p.Listing(News));
    }

    [Fact]
    public void Listing_off_hides_all()
    {
        var p = On(("Limits__ListChats", "off"), ("Chats__0__Id", "111"), ("Chats__0__Read", "true"));
        Assert.Null(p.Listing(Friend));
    }

    [Fact]
    public void Listing_names_all_shows_unreadable_chats()
    {
        var p = On(("Limits__ListChats", "names_all"));
        Assert.All(All, c => Assert.NotNull(p.Listing(c)));
    }

    [Fact]
    public void Listing_allowed_only_needs_global_read()
    {
        var cfg = PolicyConfig.FromPairs(("Enabled", "true"), ("Chats__0__Id", "111"), ("Chats__0__Read", "true"));
        Assert.Null(new PolicyEngine(cfg).Listing(Friend));
    }
}
