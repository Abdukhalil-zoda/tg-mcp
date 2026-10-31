using System.Text.Encodings.Web;
using System.Text.Json;
using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>Limits join_chat calls (limits.joinsPerHour).</summary>
public interface IJoinLimiter
{
    /// <summary>Takes one join token; false if the caller must be refused.</summary>
    bool TryAcquire();
}

/// <summary>Token bucket of limits.joinsPerHour tokens per hour.</summary>
public sealed class JoinRateLimiter : IJoinLimiter
{
    private readonly TokenBucket _bucket;

    /// <summary>Creates the limiter.</summary>
    public JoinRateLimiter(int perHour, TimeProvider time) => _bucket = new TokenBucket(perHour, TimeSpan.FromHours(1), time);

    /// <inheritdoc />
    public bool TryAcquire() => _bucket.TryAcquire();
}

/// <summary>Shared join logic: target parsing, deny check on the resolved chat, rule snippet.</summary>
internal static class JoinSupport
{
    private static readonly JsonSerializerOptions Quote = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The target, or an <see cref="ArgumentException"/> with the reason.</summary>
    public static JoinTarget Parse(string target) =>
        JoinTarget.TryParse(target, out var t, out var error) ? t! : throw new ArgumentException(error, nameof(target));

    /// <summary>The policy view of a previewed chat (id 0 if unknown).</summary>
    public static ChatInfo Info(TgChatPreview p) => new(p.ChatId ?? 0, p.Title, p.Usernames, p.Kind);

    /// <summary>True if a deny rule (id, username, type or folder) matches the previewed chat.</summary>
    public static bool HardDenied(IPolicy policy, TgChatPreview p) => policy.Effective(Info(p)).Source == DecisionSource.Deny;

    /// <summary>The chat rule the owner would add to read the joined chat.</summary>
    public static string RuleSnippet(long chatId, string title) =>
        string.IsNullOrEmpty(title)
            ? $"{{ \"id\": {chatId}, \"read\": true }}"
            : $"{{ \"id\": {chatId}, \"name\": {JsonSerializer.Serialize(title, Quote)}, \"read\": true }}";

    /// <summary>Client-facing text for a denied join.</summary>
    public static string Denied(JoinDecision d, PolicyConfig config) => d switch
    {
        { Global: { } g } => Denials.Message(config, g, null),
        { Mode: WriteMode.Off } => "Joining chats is off. To let the assistant propose joins that you approve, add: "
            + "\"features\": { \"join\": true } and \"join\": { \"mode\": \"draft\" }",
        { Reason: "join.allow is empty" } => "join.mode is \"on\" but \"join.allow\" is empty, so nothing may be joined. "
            + "Add the allowed targets, e.g. \"join\": { \"mode\": \"on\", \"allow\": [\"@example_channel\"] }, or use \"mode\": \"draft\".",
        _ => "This target is not in \"join.allow\". To join it, add it to \"join.allow\" (e.g. \"@example_channel\" or \"+<invite hash>\"), "
            + "or switch to \"join\": { \"mode\": \"draft\" } to approve joins one by one.",
    };

    /// <summary>Audit reason without the invite hash.</summary>
    public static string AuditReason(JoinTarget t, WriteMode mode, string outcome) =>
        $"{t.Kind.ToString().ToLowerInvariant()} {t.AuditRef}, mode {Names.Snake(mode)}, {outcome}";
}

/// <summary>
/// join_chat: joins a group or channel by @username, t.me link or invite link. Needs features.join and join.mode:
/// off = denied, draft = saved for <c>tgmcp approve</c>, on = joined if join.allow matches. Joining grants no read
/// or write: the chat still falls under "defaults".
/// </summary>
public sealed class JoinChatTool
{
    private readonly ToolContext _c;
    private readonly IDraftStore _drafts;
    private readonly IJoinLimiter _limiter;

    /// <summary>Creates the tool.</summary>
    public JoinChatTool(ToolContext context, IDraftStore drafts, IJoinLimiter limiter)
    {
        _c = context;
        _drafts = drafts;
        _limiter = limiter;
    }

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string target, CancellationToken ct) =>
        _c.Runner.RunAsync("join_chat", [Need.Join], async token =>
        {
            var t = JoinSupport.Parse(target);
            var config = _c.Policy.Config;
            var jd = _c.Policy.CheckJoin(t);
            if (!jd.Allowed)
            {
                return new ToolOutcome(ToolResult.Error(JoinSupport.Denied(jd, config)), null, 0, false, "join",
                    JoinSupport.AuditReason(t, jd.Mode, "denied: " + jd.Reason));
            }
            var preview = await _c.Telegram.PreviewJoinAsync(t, token).ConfigureAwait(false);
            if (JoinSupport.HardDenied(_c.Policy, preview))
            {
                return new ToolOutcome(ToolResult.Error(Denials.HardDeny), preview.ChatId, 0, false, "deny",
                    JoinSupport.AuditReason(t, jd.Mode, "denied by rule"));
            }
            if (!preview.Joinable)
                return ToolOutcome.Failed("the target is not a group or channel", preview.ChatId, "join");
            if (preview.AlreadyMember)
            {
                var already = new
                {
                    status = "already_member",
                    chat_id = preview.ChatId,
                    title = preview.Title,
                    note = ToolResult.ThirdPartyNote,
                };
                return new ToolOutcome(ToolResult.Ok(already), preview.ChatId, 0, true, "join", JoinSupport.AuditReason(t, jd.Mode, "already member"));
            }
            if (!_limiter.TryAcquire())
                return ToolOutcome.Refused($"join limit reached (limits.joinsPerHour={config.JoinsPerHour})", preview.ChatId, "limits");

            if (jd.Mode == WriteMode.Draft)
            {
                var draft = await _drafts.SaveJoinAsync(preview.Title,
                    new PendingJoin(t.Kind, t.Value, preview.ChatId, preview.Type, preview.MemberCount), token).ConfigureAwait(false);
                var payload = new
                {
                    status = "draft",
                    draft_id = draft.Id,
                    title = preview.Title,
                    type = preview.Type,
                    member_count = preview.MemberCount,
                    join_request_needed = preview.JoinRequestNeeded,
                    message = $"join saved as draft {draft.Id}; the owner joins with `tgmcp approve {draft.Id}` (nothing was joined)",
                    note = ToolResult.ThirdPartyNote,
                };
                return new ToolOutcome(ToolResult.Ok(payload), preview.ChatId, 1, true, "join", JoinSupport.AuditReason(t, jd.Mode, $"draft {draft.Id} saved"));
            }

            var joined = await _c.Telegram.JoinAsync(t, token).ConfigureAwait(false);
            return new ToolOutcome(ToolResult.Ok(JoinChatTool.Result(joined, preview)), joined.ChatId, 1, true, "join",
                JoinSupport.AuditReason(t, jd.Mode, joined.Status));
        }, ct);

    /// <summary>Payload after a join: status, chat id, and the rule the owner would add to read it.</summary>
    internal static object Result(TgJoinResult joined, TgChatPreview? preview)
    {
        var id = joined.ChatId ?? preview?.ChatId;
        var title = string.IsNullOrEmpty(joined.Title) ? preview?.Title ?? "" : joined.Title;
        return new
        {
            status = joined.Status,
            chat_id = id,
            title,
            type = preview?.Type,
            access = "Joining grants no read or write access; the chat falls under \"defaults\".",
            rule_to_add = id is long cid ? JoinSupport.RuleSnippet(cid, title) : null,
            hint = id is long ? "To let the assistant read it, the owner adds rule_to_add to \"chats\"." : "The chat id is known once the join request is approved.",
            note = ToolResult.ThirdPartyNote,
        };
    }
}

/// <summary>preview_chat: what a join target is (title, type, members, join request needed) without joining. Needs features.join.</summary>
public sealed class PreviewChatTool
{
    private readonly ToolContext _c;

    /// <summary>Creates the tool.</summary>
    public PreviewChatTool(ToolContext context) => _c = context;

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string target, CancellationToken ct) =>
        _c.Runner.RunAsync("preview_chat", [Need.Join], async token =>
        {
            var t = JoinSupport.Parse(target);
            var p = await _c.Telegram.PreviewJoinAsync(t, token).ConfigureAwait(false);
            if (JoinSupport.HardDenied(_c.Policy, p))
            {
                return new ToolOutcome(ToolResult.Error(Denials.HardDeny), p.ChatId, 0, false, "deny",
                    JoinSupport.AuditReason(t, _c.Policy.Config.JoinMode, "preview denied by rule"));
            }
            var payload = new
            {
                note = ToolResult.ThirdPartyNote,
                chat_id = p.ChatId,
                title = p.Title,
                type = p.Type,
                member_count = p.MemberCount,
                join_request_needed = p.JoinRequestNeeded,
                already_member = p.AlreadyMember,
                join_mode = Names.Snake(_c.Policy.Config.JoinMode),
            };
            return new ToolOutcome(ToolResult.Ok(payload), p.ChatId, 1, true, "join", JoinSupport.AuditReason(t, _c.Policy.Config.JoinMode, "previewed"));
        }, ct);
}
