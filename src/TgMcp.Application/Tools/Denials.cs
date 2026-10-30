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
                return $"{HardDeny}: chat folder deny rules could not be evaluated, so everything is denied (the operator must check the TGMCP_Deny__N__Folder rules)";
            case DecisionSource.Global or DecisionSource.Limits:
                return $"{HardDeny}: {d.Reason}";
        }
        if (chat is null) return $"{HardDeny}: {d.Reason}";

        var who = Describe(chat);
        var k = d.RuleIndex;
        var n = NextIndex(config);
        return kind switch
        {
            DenialKind.TooOld => k is int i
                ? $"The message is older than the {d.SinceDays}-day limit for {who}. To widen it change TGMCP_Chats__{i}__SinceDays."
                : $"The message is older than the {d.SinceDays}-day limit for {who}. To widen it change TGMCP_Defaults__SinceDays or add a rule TGMCP_Chats__{n}__Id={chat.Id} with its own SinceDays.",
            DenialKind.Topic => k is int i
                ? (topicId is int t
                    ? $"Topic {t} of {who} is not allowed. To allow it add {t} to TGMCP_Chats__{i}__Topics."
                    : $"The message is outside the allowed topics of {who} (TGMCP_Chats__{i}__Topics).")
                : $"The message is outside the allowed topics of {who}.",
            _ => Permission(d, chat, who, k, n),
        };
    }

    private static string Permission(Decision d, TgChat chat, string who, int? k, int n)
    {
        if (d.Missing is Need.Read or null)
        {
            return k is int i
                ? $"{who} is not allowed to be read (rule TGMCP_Chats__{i} has Read off). To grant read-only access set: TGMCP_Chats__{i}__Read=true"
                : $"{who} is not allowed. To grant read-only access add: TGMCP_Chats__{n}__Id={chat.Id} and TGMCP_Chats__{n}__Read=true";
        }
        if (Names.AsFeature(d.Missing.Value) is Feature f)
        {
            var name = Names.Snake(f);
            if (k is int i)
            {
                var list = string.Join(",", d.FeatureSet.Append(f).Select(Names.Snake).Distinct().Order(StringComparer.Ordinal));
                return $"{who} does not have the '{name}' feature. To grant it set: TGMCP_Chats__{i}__Features={list}";
            }
            return $"{who} does not have the '{name}' feature. To grant it add: TGMCP_Chats__{n}__Id={chat.Id}, TGMCP_Chats__{n}__Read=true and TGMCP_Chats__{n}__Features={name}";
        }
        return $"{who} is not allowed: {d.Reason}";
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

    /// <summary>The next free <c>Chats__N</c> index (after all existing rules).</summary>
    public static int NextIndex(PolicyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Chats.Count == 0 ? 0 : config.Chats.Max(r => r.Index) + 1;
    }
}
