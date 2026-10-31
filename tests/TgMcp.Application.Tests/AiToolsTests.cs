using System.Text.Json;
using TgMcp.Application.Telegram;
using TgMcp.Application.Tools;
using TgMcp.Domain;
using static TgMcp.Application.Tests.Harness;

namespace TgMcp.Application.Tests;

public class AiToolsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TgChat Friend = new(111, "Friend", ["friend"], ChatKind.Private);

    private static Harness H(params (string, string)[] extra)
    {
        var h = new Harness([("Enabled", "true"), ("Features__Read", "true"), ("Features__Summarize", "true"), ("Features__Translate", "true"), .. extra]);
        Assert.True(h.Policy.Config.IsValid, string.Join("; ", h.Policy.Config.Errors));
        h.Telegram.Add(Friend, Msg(111, 5, "a long text"), Msg(111, 1, "old", daysAgo: 30));
        return h;
    }

    private static readonly (string, string)[] Both = [("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Features", "summarize,translate")];

    [Fact]
    public async Task Summarize_returns_text_marked_as_third_party()
    {
        var h = H(Both);
        var root = JsonDocument.Parse((await h.Ai.SummarizeAsync("111", 5, null, Ct)).Text).RootElement;
        Assert.Equal("short summary", root.GetProperty("text").GetString());
        Assert.Equal(ToolResult.ThirdPartyNote, root.GetProperty("note").GetString());
        Assert.Equal("message 5, result length 13", h.LastAudit.Reason);
        Assert.DoesNotContain("summary", h.LastAudit.Reason.Replace("result", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summarize_can_translate_and_translate_normalizes_the_language()
    {
        var h = H(Both);
        await h.Ai.SummarizeAsync("111", 5, "EN", Ct);
        var root = JsonDocument.Parse((await h.Ai.TranslateAsync("111", 5, "pt_BR", Ct)).Text).RootElement;
        Assert.Equal("pt-br", root.GetProperty("language").GetString());
        Assert.Equal([("summary", 111L, 5L, (string?)"en"), ("translation", 111L, 5L, "pt-br")], h.Telegram.AiCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("english")]
    [InlineData("e")]
    [InlineData("en-")]
    public async Task Bad_language_codes_are_rejected(string lang)
    {
        var h = H(Both);
        Assert.StartsWith("bad argument", (await h.Ai.TranslateAsync("111", 5, lang, Ct)).Text, StringComparison.Ordinal);
        Assert.Empty(h.Telegram.AiCalls);
    }

    [Fact]
    public async Task Each_tool_needs_its_own_feature()
    {
        var h = H(("Chats__0__Id", "111"), ("Chats__0__Read", "true"), ("Chats__0__Features", "summarize"));
        Assert.False((await h.Ai.SummarizeAsync("111", 5, null, Ct)).IsError);
        Assert.Contains("'translate' feature", (await h.Ai.TranslateAsync("111", 5, "en", Ct)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Global_switch_needs_read_and_deny_wins()
    {
        var off = new Harness([("Enabled", "true"), ("Features__Read", "true"), .. Both]);
        Assert.Contains("\"features.summarize\" is off", (await off.Ai.SummarizeAsync("111", 5, null, Ct)).Text, StringComparison.Ordinal);
        Assert.Equal(0, off.Telegram.Calls);
        var noRead = H(("Chats__0__Id", "111"), ("Chats__0__Features", "summarize"));
        Assert.Contains("\"read\": false", (await noRead.Ai.SummarizeAsync("111", 5, null, Ct)).Text, StringComparison.Ordinal);
        var deny = H([.. Both, ("Deny__0__Id", "111")]);
        Assert.Equal(Denials.HardDeny, (await deny.Ai.TranslateAsync("111", 5, "en", Ct)).Text);
    }

    [Fact]
    public async Task Since_days_apply_and_premium_errors_pass_through()
    {
        var h = H([.. Both, ("Chats__0__SinceDays", "7")]);
        Assert.Contains("older than", (await h.Ai.SummarizeAsync("111", 1, null, Ct)).Text, StringComparison.Ordinal);
        h.Telegram.AiFailure = new TelegramCallException(400, "PREMIUM_ACCOUNT_REQUIRED");
        var r = await h.Ai.TranslateAsync("111", 5, "en", Ct);
        Assert.Contains("PREMIUM_ACCOUNT_REQUIRED", r.Text, StringComparison.Ordinal);
        Assert.Equal("telegram error 400", h.LastAudit.Reason);
    }

    [Fact]
    public async Task Rate_limit_applies()
    {
        var h = H([.. Both, ("Limits__RatePerMinute", "1")]);
        Assert.False((await h.Ai.SummarizeAsync("111", 5, null, Ct)).IsError);
        Assert.Contains("rate limit", (await h.Ai.SummarizeAsync("111", 5, null, Ct)).Text, StringComparison.Ordinal);
    }
}
