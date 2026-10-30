using System.Text.Encodings.Web;
using System.Text.Json;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>Why a chat-level check failed after the chat itself was allowed by permissions.</summary>
public enum DenialKind
{
    /// <summary>A permission (read or a feature) is missing.</summary>
    Permission,
    /// <summary>The message is older than SinceDays.</summary>
    TooOld,
    /// <summary>The message or topic is outside Topics.</summary>
    Topic,
}

/// <summary>
/// Builds the denial text the client sees. Denials by permissions name the chat and give the operator a
/// one-line config hint; Deny rules are deliberate hard blocks and reveal nothing beyond "denied by policy".
/// </summary>
public static class Denials
{
    /// <summary>The only text returned for chats hit by a Deny rule.</summary>
    public const string HardDeny = "denied by policy";

    private static readonly JsonSerializerOptions Quote = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Client-facing message for <paramref name="d"/>.</summary>
    public static string Message(PolicyConfig config, Decision d, TgChat? chat, DenialKind kind = DenialKind.Permission, int? topicId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(d);
        switch (d.Source)
        {
            case DecisionSource.Deny:
                return HardDeny;
            case DecisionSource.Global when d.Reason.StartsWith("folder deny", StringComparison.Ordinal):
                // Do not echo which folders exist or are denied.
                return $"{HardDeny}: chat folder deny rules could not be evaluated, so everything is denied (the operator must check the \"folder\" rules in \"deny\")";
            case DecisionSource.Global or DecisionSource.Limits:
                return $"{HardDeny}: {d.Reason}";
        }
        if (chat is null) return $"{HardDeny}: {d.Reason}";

        var who = Describe(chat);
        var rule = d.RuleIndex is int k ? RuleRef(config, k) : null;
        return kind switch
        {
            DenialKind.TooOld => rule is not null
                ? $"The message is older than the {d.SinceDays}-day limit for {who}. To widen it change \"sinceDays\" in {rule}."
                : $"The message is older than the {d.SinceDays}-day limit for {who}. To widen it change \"defaults.sinceDays\", or add a rule for this chat to \"chats\" with its own \"sinceDays\".",
            DenialKind.Topic => rule is not null
                ? (topicId is int t
                    ? $"Topic {t} of {who} is not allowed. To allow it add {t} to \"topics\" in {rule}."
                    : $"The message is outside the allowed \"topics\" of {who} ({rule}).")
                : $"The message is outside the allowed topics of {who}.",
            _ => Permission(d, chat, who, rule),
        };
    }

    private static string Permission(Decision d, TgChat chat, string who, string? rule)
    {
        if (d.Missing is Need.Read or null)
        {
            return rule is not null
                ? $"{who} is not allowed to be read: {rule} has \"read\": false. To grant read access set \"read\": true in that rule."
                : $"{who} is not allowed. To grant read-only access add to \"chats\": {Snippet(chat, "\"read\": true")}";
        }
        if (Names.AsFeature(d.Missing.Value) is Feature f)
        {
            var name = Names.Snake(f);
            if (rule is not null)
            {
                var list = string.Join(", ", d.FeatureSet.Append(f).Select(Names.Snake).Distinct().Order(StringComparer.Ordinal).Select(x => $"\"{x}\""));
                return $"{who} does not have the '{name}' feature. To grant it set \"features\": [{list}] in {rule}.";
            }
            return $"{who} does not have the '{name}' feature. To grant it add to \"chats\": {Snippet(chat, $"\"read\": true, \"features\": [\"{name}\"]")}";
        }
        return $"{who} is not allowed: {d.Reason}";
    }

    /// <summary>A ready-to-paste rule: <c>{ "id": -100123, "name": "Title", ... }</c>.</summary>
    private static string Snippet(TgChat chat, string rest)
    {
        var name = string.IsNullOrEmpty(chat.Title) ? "" : $", \"name\": {JsonSerializer.Serialize(chat.Title, Quote)}";
        return $"{{ \"id\": {chat.Id}{name}, {rest} }}";
    }

    private static string RuleRef(PolicyConfig config, int index)
    {
        var r = config.Chats.FirstOrDefault(c => c.Index == index);
        return r?.Name is string n ? $"rule chats[{index}] ({JsonSerializer.Serialize(n, Quote)})" : $"rule chats[{index}]";
    }

    /// <summary>"Chat -100123 ("Title", @name)", using only data already resolved.</summary>
    public static string Describe(TgChat chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(chat.Title)) parts.Add(JsonSerializer.Serialize(chat.Title, Quote));
        if (chat.Usernames is [var u, ..]) parts.Add("@" + u);
        return parts.Count == 0 ? $"Chat {chat.Id}" : $"Chat {chat.Id} ({string.Join(", ", parts)})";
    }
}
