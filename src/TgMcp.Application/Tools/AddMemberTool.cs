using System.Globalization;
using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>Limits add_member calls (limits.addsPerHour and limits.addsPerDay).</summary>
public interface IAddMemberLimiter
{
    /// <summary>Takes one token from both buckets, or returns the limit that refused the call.</summary>
    PaceLimit TryAcquire();
}

/// <summary>Token buckets of limits.addsPerHour tokens per hour and limits.addsPerDay tokens per day.</summary>
public sealed class AddMemberRateLimiter : PacedLimiter, IAddMemberLimiter
{
    /// <summary>Creates the limiter.</summary>
    public AddMemberRateLimiter(int perHour, int perDay, TimeProvider time) : base(perHour, perDay, time)
    {
    }
}

/// <summary>Shared add_member logic: user parsing, denials, Telegram error texts, the add itself.</summary>
internal static class AddMemberSupport
{
    /// <summary>Resolves a user id or @username; throws <see cref="ArgumentException"/> with the reason.</summary>
    public static async Task<TgUser> ResolveUserAsync(ITelegramClient telegram, string user, CancellationToken ct)
    {
        var s = (user ?? "").Trim();
        if (long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            return await telegram.GetUserAsync(id, ct).ConfigureAwait(false)
                ?? throw new ArgumentException("unknown user id: the owner's account must already know this user (a contact, or someone seen in a shared chat); use their @username instead", nameof(user));
        }
        var u = Names.NormalizeUsername(s);
        if (u.Length == 0 || !u.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("user must be a numeric user id or a @username", nameof(user));
        return await telegram.FindUserAsync(u, ct).ConfigureAwait(false)
            ?? throw new ArgumentException("no Telegram user with that username", nameof(user));
    }

    /// <summary>@username or the display name.</summary>
    public static string Label(TgUser u) => u.Username is { } n ? "@" + n : u.Name;

    /// <summary>Audit reason: user id, mode and outcome (no names).</summary>
    public static string AuditReason(long userId, WriteMode mode, string outcome) =>
        $"user {userId}, mode {Names.Snake(mode)}, {outcome}";

    /// <summary>Client-facing text for a denied add_member, saying what the owner must change.</summary>
    public static string Denied(JoinDecision d, PolicyConfig config, TgChat? chat) => d switch
    {
        { Global: { } g } => Denials.Message(config, g, null),
        { Mode: WriteMode.Off } => "Adding members is off. To let the assistant propose additions that you approve, add: "
            + "\"features\": { \"addMember\": true } and \"addMember\": { \"mode\": \"draft\" }",
        { Reason: "addMember.chats is empty" } => "addMember.mode is \"on\" but \"addMember.chats\" is empty, so no chat may get new members. "
            + "Add the allowed groups/channels, e.g. \"addMember\": { \"mode\": \"on\", \"chats\": [-1001234567890] }, or use \"mode\": \"draft\".",
        _ => $"{(chat is null ? "This chat" : Denials.Describe(chat))} is not in \"addMember.chats\". To add members there directly, add "
            + $"{(chat is null ? "its id" : chat.Id.ToString(CultureInfo.InvariantCulture))} to \"addMember.chats\", or switch to \"addMember\": {{ \"mode\": \"draft\" }} to approve each addition.",
    };

    /// <summary>A friendly text for common Telegram refusals, or null.</summary>
    public static string? Friendly(TelegramCallException e)
    {
        var m = e.TdMessage;
        bool Has(string s) => m.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("USER_PRIVACY_RESTRICTED"))
            return "Telegram refused (USER_PRIVACY_RESTRICTED): the user's privacy settings don't allow you to add them. Send them an invite link instead.";
        if (Has("USER_NOT_MUTUAL_CONTACT"))
            return "Telegram refused (USER_NOT_MUTUAL_CONTACT): this user can only be added by a mutual contact. Send them an invite link instead.";
        if (Has("CHAT_ADMIN_REQUIRED") || Has("not enough rights") || Has("CHAT_WRITE_FORBIDDEN"))
            return "Telegram refused (CHAT_ADMIN_REQUIRED): you lack the right to add members in this chat. An admin must give you the \"Add members\" right.";
        if (Has("PEER_FLOOD"))
            return "Telegram refused (PEER_FLOOD): the account is temporarily limited for adding people. Stop adding members for at least a day; "
                + "continuing can get the account restricted. Consider lowering limits.addsPerHour / limits.addsPerDay.";
        if (Has("USERS_TOO_MUCH")) return "Telegram refused (USERS_TOO_MUCH): the chat has reached its member limit.";
        if (Has("USER_CHANNELS_TOO_MUCH")) return "Telegram refused (USER_CHANNELS_TOO_MUCH): the user is already in too many groups and channels.";
        if (Has("USER_KICKED") || Has("USER_BANNED_IN_CHANNEL"))
            return "Telegram refused (USER_KICKED): the user was removed from this chat; an admin must unban them first.";
        if (Has("BOT_GROUPS_BLOCKED")) return "Telegram refused (BOT_GROUPS_BLOCKED): this bot can't be added to groups.";
        return null;
    }

    /// <summary>Short Telegram error name for the audit log (e.g. "PEER_FLOOD"), never message content.</summary>
    public static string ErrorName(TelegramCallException e)
    {
        var m = e.TdMessage;
        var end = m.IndexOfAny([':', ' ']);
        var name = end > 0 ? m[..end] : m;
        return name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_') ? name : $"code {e.Code}";
    }

    /// <summary>Hard deny on the chat or on the user (opaque).</summary>
    public static bool HardDenied(IPolicy policy, TgChat chat, TgUser user) =>
        policy.Effective(chat.Info).Source == DecisionSource.Deny || policy.Effective(user.Info).Source == DecisionSource.Deny;

    /// <summary>Adds the user and maps the outcome; "already a member" counts as success.</summary>
    public static async Task<ToolOutcome> AddAsync(ToolContext c, TgChat chat, TgUser user, WriteMode mode, string rule, string auditPrefix, CancellationToken ct)
    {
        string status;
        try
        {
            await c.Telegram.AddChatMemberAsync(chat.Id, user.Id, ct).ConfigureAwait(false);
            status = "added";
        }
        catch (TelegramCallException e) when (e.TdMessage.Contains("USER_ALREADY_PARTICIPANT", StringComparison.OrdinalIgnoreCase))
        {
            status = "already_member";
        }
        catch (TelegramCallException e) when (Friendly(e) is { } friendly)
        {
            return new ToolOutcome(ToolResult.Error(friendly), chat.Id, 0, true, "telegram", AuditReason(user.Id, mode, auditPrefix + "telegram error " + ErrorName(e)));
        }
        var payload = new
        {
            status,
            chat_id = chat.Id,
            title = chat.Title,
            user_id = user.Id,
            user = Label(user),
            note = ToolResult.ThirdPartyNote,
        };
        return new ToolOutcome(ToolResult.Ok(payload), chat.Id, 1, true, rule, AuditReason(user.Id, mode, auditPrefix + status));
    }
}

/// <summary>
/// add_member: adds one user (id or @username) to a group or channel as the owner. Needs features.addMember and
/// addMember.mode: off = denied, draft = saved for <c>tgmcp approve</c>, on = added if addMember.chats names the chat.
/// Deny rules on the chat or on the user win. limits.addsPerHour and limits.addsPerDay apply (added or drafted).
/// </summary>
public sealed class AddMemberTool
{
    private readonly ToolContext _c;
    private readonly IDraftStore _drafts;
    private readonly IAddMemberLimiter _limiter;

    /// <summary>Creates the tool.</summary>
    public AddMemberTool(ToolContext context, IDraftStore drafts, IAddMemberLimiter limiter)
    {
        _c = context;
        _drafts = drafts;
        _limiter = limiter;
    }

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string chat, string user, CancellationToken ct) =>
        _c.Runner.RunAsync("add_member", [Need.AddMember], async token =>
        {
            var config = _c.Policy.Config;
            if (config.AddMemberMode == WriteMode.Off)
            {
                var off = new JoinDecision(false, WriteMode.Off, "addMember.mode is off");
                return new ToolOutcome(ToolResult.Error(AddMemberSupport.Denied(off, config, null)), null, 0, false, "add_member", "denied: " + off.Reason);
            }
            var target = await _c.Chats.ResolveAsync(chat, token).ConfigureAwait(false);
            var ad = _c.Policy.CheckAddMember(target.Info);
            if (_c.Policy.Effective(target.Info).Source == DecisionSource.Deny)
                return new ToolOutcome(ToolResult.Error(Denials.HardDeny), target.Id, 0, false, "deny", "add_member denied by rule");
            if (!ad.Allowed)
            {
                return new ToolOutcome(ToolResult.Error(AddMemberSupport.Denied(ad, config, target)), target.Id, 0, false, "add_member",
                    $"mode {Names.Snake(ad.Mode)}, denied: {ad.Reason}");
            }
            if (target.Kind is not (ChatKind.Group or ChatKind.Channel))
                return ToolOutcome.Failed("members can only be added to groups and channels", target.Id, "add_member");
            var who = await AddMemberSupport.ResolveUserAsync(_c.Telegram, user, token).ConfigureAwait(false);
            if (AddMemberSupport.HardDenied(_c.Policy, target, who))
                return new ToolOutcome(ToolResult.Error(Denials.HardDeny), target.Id, 0, false, "deny", AddMemberSupport.AuditReason(who.Id, ad.Mode, "denied by rule"));
            if (_limiter.TryAcquire() is not PaceLimit.None and var hit)
            {
                return ToolOutcome.Refused(Pace.Message("add_member", hit, "limits.addsPerHour", config.AddsPerHour, "limits.addsPerDay", config.AddsPerDay),
                    target.Id, "limits");
            }

            if (ad.Mode == WriteMode.Draft)
            {
                var draft = await _drafts.SaveAddMemberAsync(target.Title, new PendingAddMember(target.Id, who.Id, AddMemberSupport.Label(who)), token).ConfigureAwait(false);
                var payload = new
                {
                    status = "draft",
                    draft_id = draft.Id,
                    chat_id = target.Id,
                    title = target.Title,
                    user_id = who.Id,
                    user = AddMemberSupport.Label(who),
                    message = $"saved as draft {draft.Id}; the owner adds the user with `tgmcp approve {draft.Id}` (nobody was added)",
                    note = ToolResult.ThirdPartyNote,
                };
                return new ToolOutcome(ToolResult.Ok(payload), target.Id, 1, true, "add_member", AddMemberSupport.AuditReason(who.Id, ad.Mode, $"draft {draft.Id} saved"));
            }
            return await AddMemberSupport.AddAsync(_c, target, who, ad.Mode, "add_member", "", token).ConfigureAwait(false);
        }, ct);
}
