using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Drafts;

/// <summary>
/// Something the assistant proposed that only the owner may carry out: a message (<c>"write": "draft"</c>)
/// or a join (<c>"join": { "mode": "draft" }</c>).
/// </summary>
/// <param name="Id">Short draft id.</param>
/// <param name="CreatedUtc">When it was saved.</param>
/// <param name="ChatTitle">Chat title at the time (for the owner's overview).</param>
/// <param name="Message">The message to send (kind "message").</param>
/// <param name="Kind">"message" or "join".</param>
/// <param name="Join">The join to perform (kind "join").</param>
public sealed record Draft(string Id, DateTimeOffset CreatedUtc, string ChatTitle, OutgoingMessage? Message,
    string Kind = Draft.MessageKind, PendingJoin? Join = null)
{
    /// <summary>Kind of a message draft.</summary>
    public const string MessageKind = "message";

    /// <summary>Kind of a join draft.</summary>
    public const string JoinKind = "join";
}

/// <summary>A join waiting for the owner's approval.</summary>
/// <param name="TargetKind">Username or invite.</param>
/// <param name="TargetValue">Username, or the invite hash (stored owner-only; never audited).</param>
/// <param name="ChatId">Chat id if known at preview time.</param>
/// <param name="Type">group / supergroup / channel.</param>
/// <param name="MemberCount">Member count at preview time.</param>
public sealed record PendingJoin(JoinTargetKind TargetKind, string TargetValue, long? ChatId, string Type, int MemberCount)
{
    /// <summary>The target to join.</summary>
    public JoinTarget Target => new(TargetKind, TargetValue);
}

/// <summary>Owner-only storage for drafts. Draft text is message content: never log it.</summary>
public interface IDraftStore
{
    /// <summary>Saves a new draft and returns it with its id.</summary>
    Task<Draft> SaveAsync(string chatTitle, OutgoingMessage message, CancellationToken ct);

    /// <summary>Saves a new join draft.</summary>
    Task<Draft> SaveJoinAsync(string chatTitle, PendingJoin join, CancellationToken ct);

    /// <summary>All drafts, oldest first.</summary>
    Task<IReadOnlyList<Draft>> ListAsync(CancellationToken ct);

    /// <summary>One draft, or null.</summary>
    Task<Draft?> GetAsync(string id, CancellationToken ct);

    /// <summary>Deletes a draft; false if it did not exist.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct);
}
