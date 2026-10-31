using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>Limits first messages to new people by phone (limits.newChatsPerDay).</summary>
public interface INewChatLimiter
{
    /// <summary>Takes one token; false if the caller must be refused.</summary>
    bool TryAcquire();
}

/// <summary>Token bucket of limits.newChatsPerDay tokens per 24 hours.</summary>
public sealed class NewChatRateLimiter : INewChatLimiter
{
    private readonly TokenBucket _bucket;

    /// <summary>Creates the limiter.</summary>
    public NewChatRateLimiter(int perDay, TimeProvider time) => _bucket = new TokenBucket(perDay, TimeSpan.FromDays(1), time);

    /// <inheritdoc />
    public bool TryAcquire() => _bucket.TryAcquire();
}

/// <summary>
/// send_message with a phone number: gated by features.send, contacts.mode and the env-only TGMCP_ContactsAllow list.
/// draft (recommended) stores the message for <c>tgmcp approve</c> without importing anything; on imports exactly one
/// contact and sends. Deny rules and an explicit chat rule with write off on the resolved chat still win.
/// The number never appears in the audit log (hash prefix only).
/// </summary>
internal static class PhoneMessages
{
    public static async Task<ToolOutcome> SendAsync(ToolContext c, IDraftStore drafts, INewChatLimiter limiter, string phone, string? contactName,
        string text, bool disableNotification, CancellationToken ct)
    {
        if (!PhoneNumber.TryNormalize(phone, out var e164)) throw new ArgumentException("phone must be an E.164 number like +15551234567", nameof(phone));
        var name = string.IsNullOrWhiteSpace(contactName) ? "New contact" : contactName.Trim();
        if (name.Length > 64) throw new ArgumentException("contact_name is too long (64 characters at most)", nameof(contactName));
        var reference = PhoneNumber.AuditRef(e164!);
        var pd = c.Policy.CheckPhone(e164!);
        if (!pd.Allowed) return Denied(c, pd, reference);
        if (!limiter.TryAcquire())
            return ToolOutcome.Refused($"limit for messages to new people reached (limits.newChatsPerDay={c.Policy.Config.NewChatsPerDay})", null, "limits");

        var pending = new PendingPhoneMessage(e164!, name, text, disableNotification);
        if (pd.Mode == WriteMode.Draft)
        {
            var draft = await drafts.SavePhoneAsync(pending, ct).ConfigureAwait(false);
            var payload = new
            {
                status = "draft",
                draft_id = draft.Id,
                message = $"message to a phone number saved as draft {draft.Id}; the owner sends it with `tgmcp approve {draft.Id}` (no contact was imported, nothing was sent)",
            };
            return new ToolOutcome(ToolResult.Ok(payload), null, 1, true, "contacts", $"{reference}, draft {draft.Id} saved, length {text.Length}");
        }
        return await DeliverAsync(c, pending, reference, "sent", ct).ConfigureAwait(false);
    }

    /// <summary>Approves a phone-message draft: re-checks the gate (mode not off, number still allowed), then delivers.</summary>
    public static async Task<ToolOutcome> ApproveAsync(ToolContext c, Draft draft, PendingPhoneMessage pending, CancellationToken ct)
    {
        var reference = PhoneNumber.AuditRef(pending.Phone);
        var pd = c.Policy.CheckPhone(pending.Phone);
        if (!pd.Allowed) return Denied(c, pd, reference);
        return await DeliverAsync(c, pending, reference, $"draft {draft.Id} approved, sent", ct).ConfigureAwait(false);
    }

    private static async Task<ToolOutcome> DeliverAsync(ToolContext c, PendingPhoneMessage p, string reference, string outcome, CancellationToken ct)
    {
        var found = await c.Telegram.ResolvePhoneAsync(p.Phone, p.ContactName, ct).ConfigureAwait(false);
        if (found is null)
            return new ToolOutcome(ToolResult.Error("this number is not on Telegram or its owner does not allow being found by phone"), null, 0, true, "contacts", $"{reference}, not found");
        var chat = found.Chat;
        var d = c.Policy.Check(chat.Info, Need.Write);
        if (d.Source == DecisionSource.Deny)
            return new ToolOutcome(ToolResult.Error(Denials.HardDeny), chat.Id, 0, false, d.Rule, $"{reference}, denied by rule");
        if (d.Source == DecisionSource.ChatRule && !d.Allowed) return c.Deny(d, chat);
        var sent = await c.Telegram.SendMessageAsync(new OutgoingMessage(chat.Id, p.Text, 0, null, p.DisableNotification), ct).ConfigureAwait(false);
        await c.SentLog.RecordAsync(sent.ChatId, sent.MessageId, ct).ConfigureAwait(false);
        var payload = new
        {
            status = "sent",
            chat = sent.ChatId,
            message_id = sent.MessageId,
            contact_imported = found.Imported,
            access = "The new chat falls under \"defaults\"; add a rule for it to read or keep writing there.",
            rule_to_add = JoinSupport.RuleSnippet(chat.Id, chat.Title),
        };
        return new ToolOutcome(ToolResult.Ok(payload), chat.Id, 1, true, "contacts",
            $"{reference}, {outcome}{(found.Imported ? ", contact imported" : "")}, length {p.Text.Length}");
    }

    private static ToolOutcome Denied(ToolContext c, JoinDecision pd, string reference)
    {
        var text = pd switch
        {
            { Global: { } g } => Denials.Message(c.Policy.Config, g, null),
            { Mode: WriteMode.Off } => "Messaging people by phone number is off. To allow it (each message approved by you), set \"contacts\": { \"mode\": \"draft\" } "
                + "in the config file and list the allowed numbers in the environment variable TGMCP_ContactsAllow (E.164, comma-separated).",
            { Reason: "TGMCP_ContactsAllow is empty" } => "No phone numbers are allowed. The owner lists them in the environment variable TGMCP_ContactsAllow (E.164, comma-separated).",
            _ => "This number is not in TGMCP_ContactsAllow. Only the owner can add it (environment variable, E.164).",
        };
        return new ToolOutcome(ToolResult.Error(text), null, 0, false, "contacts", $"{reference}, denied: {pd.Reason}");
    }
}
