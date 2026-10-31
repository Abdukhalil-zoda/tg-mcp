using System.Text.RegularExpressions;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>
/// summarize_message / translate_message: Telegram's own AI summary and translation of one message
/// (TDLib summarizeMessage / translateMessageText). Needs read + "summarize" / "translate". These features
/// usually need Telegram Premium; Telegram's errors are passed through. Results are third-party data.
/// </summary>
public sealed partial class AiTools
{
    private readonly ToolContext _c;

    /// <summary>Creates the tools.</summary>
    public AiTools(ToolContext context) => _c = context;

    [GeneratedRegex("^[a-z]{2,3}(-[a-z0-9]{2,8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageRegex();

    /// <summary>A lowercase BCP 47-style language code (e.g. "en", "pt-br"), or an ArgumentException.</summary>
    public static string Language(string? code, string name)
    {
        var c = (code ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        return LanguageRegex().IsMatch(c) ? c : throw new ArgumentException($"{name} must be a language code such as \"en\" or \"pt-br\"", name);
    }

    /// <summary>Summarizes a message, optionally translated to <paramref name="translateTo"/>.</summary>
    public Task<ToolResult> SummarizeAsync(string chat, long messageId, string? translateTo, CancellationToken ct) =>
        RunAsync("summarize_message", Need.Summarize, chat, messageId, async (id, token) =>
        {
            var lang = string.IsNullOrWhiteSpace(translateTo) ? null : Language(translateTo, "translate_to");
            return (await _c.Telegram.SummarizeMessageAsync(id.Chat, id.Message, lang, token).ConfigureAwait(false), lang);
        }, ct);

    /// <summary>Translates a message to <paramref name="toLanguage"/>.</summary>
    public Task<ToolResult> TranslateAsync(string chat, long messageId, string toLanguage, CancellationToken ct) =>
        RunAsync("translate_message", Need.Translate, chat, messageId, async (id, token) =>
        {
            var lang = Language(toLanguage, "to_language");
            return (await _c.Telegram.TranslateMessageAsync(id.Chat, id.Message, lang, token).ConfigureAwait(false), lang);
        }, ct);

    private Task<ToolResult> RunAsync(string tool, Need feature, string chat, long messageId,
        Func<(long Chat, long Message), CancellationToken, Task<(string Text, string? Lang)>> call, CancellationToken ct) =>
        _c.Runner.RunAsync(tool, [Need.Read, feature], async token =>
        {
            if (messageId <= 0) throw new ArgumentException("message_id must be positive", nameof(messageId));
            var target = await _c.Chats.ResolveAsync(chat, token).ConfigureAwait(false);
            var d = _c.Policy.Check(target.Info, Need.Read, feature);
            if (!d.Allowed) return _c.Deny(d, target);
            var m = await _c.Telegram.GetMessageAsync(target.Id, messageId, token).ConfigureAwait(false);
            if (m is null) return ToolOutcome.Failed("message not found", target.Id, d.Rule);
            if (!d.DateOk(m.Date, _c.Time.GetUtcNow())) return _c.Deny(d.Deny("older than SinceDays"), target, DenialKind.TooOld);
            if (!d.TopicOk(m.TopicId)) return _c.Deny(d.Deny("message is outside allowed topics"), target, DenialKind.Topic, m.TopicId);
            var (text, lang) = await call((target.Id, messageId), token).ConfigureAwait(false);
            var payload = new
            {
                note = ToolResult.ThirdPartyNote,
                chat = target.Id,
                message_id = messageId,
                language = lang,
                text,
            };
            return ToolOutcome.Ok(payload, target.Id, 1, d, $"message {messageId}, result length {text.Length}{(lang is null ? "" : ", " + lang)}");
        }, ct);
}
