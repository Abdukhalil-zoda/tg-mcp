namespace TgMcp.Domain.Tests;

public class JoinTargetTests
{
    [Theory]
    [InlineData("@Example_Channel", JoinTargetKind.Username, "example_channel")]
    [InlineData("  @example_channel  ", JoinTargetKind.Username, "example_channel")]
    [InlineData("t.me/example_channel", JoinTargetKind.Username, "example_channel")]
    [InlineData("https://t.me/Example_Channel", JoinTargetKind.Username, "example_channel")]
    [InlineData("HTTPS://T.ME/example_channel/", JoinTargetKind.Username, "example_channel")]
    [InlineData("http://www.t.me/example_channel", JoinTargetKind.Username, "example_channel")]
    [InlineData("https://telegram.me/example_channel", JoinTargetKind.Username, "example_channel")]
    [InlineData("https://t.me/example_channel?start=x", JoinTargetKind.Username, "example_channel")]
    [InlineData("https://t.me/+AbCdEfGh12_-", JoinTargetKind.Invite, "AbCdEfGh12_-")]
    [InlineData("t.me/+AbCdEfGh12?utm_source=spam#x", JoinTargetKind.Invite, "AbCdEfGh12")]
    [InlineData("https://t.me/joinchat/AbCdEfGh12", JoinTargetKind.Invite, "AbCdEfGh12")]
    [InlineData("https://T.ME/JOINCHAT/AbCdEfGh12", JoinTargetKind.Invite, "AbCdEfGh12")]
    [InlineData("telegram.me/joinchat/AbCdEfGh12", JoinTargetKind.Invite, "AbCdEfGh12")]
    public void Accepts(string input, JoinTargetKind kind, string value)
    {
        Assert.True(JoinTarget.TryParse(input, out var t, out var error), error);
        Assert.Equal(kind, t!.Kind);
        Assert.Equal(value, t.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example_channel")]                    // bare name
    [InlineData("@ab")]                                // too short
    [InlineData("@1channel")]                          // must start with a letter
    [InlineData("@chan nel")]
    [InlineData("tg://join?invite=AbCdEfGh12")]        // deep links are not accepted
    [InlineData("tg://resolve?domain=example_channel")]
    [InlineData("https://t.me/+15551234567")]          // phone-number link, not an invite
    [InlineData("https://t.me/+abc")]                  // too short to be an invite
    [InlineData("https://t.me/c/1234567890/42")]       // message link
    [InlineData("https://t.me/example_channel/42")]    // message link
    [InlineData("https://t.me/addstickers/pack_name")]
    [InlineData("https://t.me/joinchat/")]
    [InlineData("https://evil.example/t.me/+AbCdEfGh12")]
    [InlineData("https://t.me.evil.example/example_channel")]
    [InlineData("javascript://t.me/example_channel")]
    [InlineData("ftp://t.me/example_channel")]
    public void Rejects(string input) => Assert.False(JoinTarget.TryParse(input, out _, out _), input);

    [Fact]
    public void Audit_reference_never_contains_the_hash()
    {
        Assert.True(JoinTarget.TryParse("https://t.me/+AbCdEfGh12", out var t, out _));
        Assert.DoesNotContain("AbCdEfGh12", t!.AuditRef, StringComparison.Ordinal);
        Assert.Matches("^invite#[0-9a-f]{8}$", t.AuditRef);
        Assert.Equal("https://t.me/+AbCdEfGh12", t.Canonical);
    }
}

public class JoinPolicyTests
{
    private static readonly JoinTarget Channel = new(JoinTargetKind.Username, "example_channel");
    private static readonly JoinTarget Other = new(JoinTargetKind.Username, "other_channel");
    private static readonly JoinTarget Invite = new(JoinTargetKind.Invite, "AbCdEfGh12");

    private static PolicyEngine P(params (string, string)[] kv) =>
        new(PolicyConfig.FromPairs([("Enabled", "true"), ("Features__Join", "true"), .. kv]));

    [Fact]
    public void Default_is_off()
    {
        var c = PolicyConfig.FromPairs();
        Assert.Equal(WriteMode.Off, c.JoinMode);
        Assert.Empty(c.JoinAllow);
        Assert.Equal(5, c.JoinsPerHour);
        var d = P().CheckJoin(Channel);
        Assert.False(d.Allowed);
        Assert.Equal("join.mode is off", d.Reason);
    }

    [Fact]
    public void Features_join_off_denies_every_mode()
    {
        foreach (var mode in new[] { "off", "draft", "on" })
        {
            var p = new PolicyEngine(PolicyConfig.FromPairs(("Enabled", "true"), ("Join__Mode", mode), ("Join__Allow", "*")));
            var d = p.CheckJoin(Channel);
            Assert.False(d.Allowed);
            Assert.NotNull(d.Global);
            Assert.Contains("\"features.join\" is off", d.Reason, StringComparison.Ordinal);
        }
    }

    [Theory]
    // mode, allow, target -> allowed
    [InlineData("off", "@example_channel", "channel", false)]
    [InlineData("draft", "", "channel", true)]            // draft needs no allow list
    [InlineData("draft", "@other_channel", "channel", true)]
    [InlineData("on", "", "channel", false)]               // empty allow = nothing
    [InlineData("on", "@example_channel", "channel", true)]
    [InlineData("on", "Example_Channel", "channel", true)] // bare, case-insensitive
    [InlineData("on", "t.me/example_channel", "channel", true)]
    [InlineData("on", "@example_channel", "other", false)]
    [InlineData("on", "+AbCdEfGh12", "invite", true)]
    [InlineData("on", "https://t.me/+AbCdEfGh12", "invite", true)]
    [InlineData("on", "+abcdefgh12", "invite", false)]     // invite hashes are case-sensitive
    [InlineData("on", "@example_channel", "invite", false)]
    [InlineData("on", "*", "other", true)]
    [InlineData("on", "*", "invite", true)]
    public void Mode_and_allow_matrix(string mode, string allow, string target, bool allowed)
    {
        var p = P(("Join__Mode", mode), ("Join__Allow", allow));
        Assert.True(p.Config.IsValid, string.Join("; ", p.Config.Errors));
        var t = target switch { "channel" => Channel, "other" => Other, _ => Invite };
        var d = p.CheckJoin(t);
        Assert.Equal(allowed, d.Allowed);
        Assert.Equal(Enum.Parse<WriteMode>(mode, true), d.Mode);
    }

    [Fact]
    public void Empty_allow_with_mode_on_says_so()
    {
        Assert.Equal("join.allow is empty", P(("Join__Mode", "on")).CheckJoin(Channel).Reason);
        Assert.Equal("target is not in join.allow", P(("Join__Mode", "on"), ("Join__Allow", "@other_channel")).CheckJoin(Channel).Reason);
    }

    [Theory]
    [InlineData("Join__Mode", "maybe")]
    [InlineData("Join__Allow", "tg://join?invite=AbCdEfGh12")]
    [InlineData("Join__Allow", "+123")]
    [InlineData("Join__Bogus", "1")]
    [InlineData("Limits__JoinsPerHour", "0")]
    public void Invalid_join_config(string key, string value) => Assert.False(PolicyConfig.FromPairs((key, value)).IsValid);

    [Fact]
    public void Invalid_allow_entries_never_echo_invite_hashes()
    {
        var c = PolicyConfig.FromPairs(("Join__Allow", "https://t.me/joinchat/bad hash"));
        Assert.False(c.IsValid);
        Assert.DoesNotContain(c.Errors, e => e.Contains("bad hash", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_shows_join_settings_and_warns_on_wildcard()
    {
        var report = string.Join("\n", ConfigReport.Describe(PolicyConfig.FromPairs(("Join__Mode", "on"), ("Join__Allow", "*,+AbCdEfGh12"))));
        Assert.Contains("join.mode = on [env]", report, StringComparison.Ordinal);
        Assert.Contains("WARNING: \"*\" allows joining anything", report, StringComparison.Ordinal);
        Assert.DoesNotContain("AbCdEfGh12", report, StringComparison.Ordinal);
        Assert.Contains("limits.joinsPerHour = 5 [default]", report, StringComparison.Ordinal);
    }
}
