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
/// <param name="Reaction">The reaction to set or remove (kind "reaction").</param>
/// <param name="Forward">The forward to perform (kind "forward").</param>
/// <param name="Edit">The edit to perform (kind "edit").</param>
/// <param name="Delete">The deletion to perform (kind "delete").</param>
public sealed record Draft(string Id, DateTimeOffset CreatedUtc, string ChatTitle, OutgoingMessage? Message,
    string Kind = Draft.MessageKind, PendingJoin? Join = null, PendingReaction? Reaction = null, PendingForward? Forward = null,
    PendingEdit? Edit = null, PendingDelete? Delete = null)
{
    /// <summary>Kind of an edit draft.</summary>
    public const string EditKind = "edit";

    /// <summary>Kind of a delete draft.</summary>
    public const string DeleteKind = "delete";

    /// <summary>Kind of a forward draft.</summary>
    public const string ForwardKind = "forward";

    /// <summary>Kind of a reaction draft.</summary>
    public const string ReactionKind = "reaction";

    /// <summary>Kind of a message draft.</summary>
    public const string MessageKind = "message";

    /// <summary>Kind of a join draft.</summary>
    public const string JoinKind = "join";
}

/// <summary>An edit waiting for the owner's approval.</summary>
/// <param name="ChatId">Chat id.</param>
/// <param name="MessageId">Own message to edit.</param>
/// <param name="Text">New plain text.</param>
public sealed record PendingEdit(long ChatId, long MessageId, string Text);

/// <summary>A deletion waiting for the owner's approval.</summary>
/// <param name="ChatId">Chat id.</param>
/// <param name="MessageIds">Own messages to delete.</param>
/// <param name="ForEveryone">Delete for all users (revoke).</param>
public sealed record PendingDelete(long ChatId, IReadOnlyList<long> MessageIds, bool ForEveryone);

/// <summary>A forward waiting for the owner's approval.</summary>
/// <param name="FromChatId">Source chat.</param>
/// <param name="MessageIds">Messages to forward.</param>
/// <param name="ToChatId">Destination chat.</param>
/// <param name="ToTopicId">Destination forum topic, if any.</param>
/// <param name="SendCopy">Send copies without the "forwarded from" header.</param>
public sealed record PendingForward(long FromChatId, IReadOnlyList<long> MessageIds, long ToChatId, int? ToTopicId, bool SendCopy);

/// <summary>A reaction waiting for the owner's approval.</summary>
/// <param name="ChatId">Chat id.</param>
/// <param name="MessageId">Message to react to.</param>
/// <param name="Emoji">Emoji to add, or null to remove the owner's reactions.</param>
public sealed record PendingReaction(long ChatId, long MessageId, string? Emoji);

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

    /// <summary>Saves a new edit draft.</summary>
    Task<Draft> SaveEditAsync(string chatTitle, PendingEdit edit, CancellationToken ct);

    /// <summary>Saves a new delete draft.</summary>
    Task<Draft> SaveDeleteAsync(string chatTitle, PendingDelete delete, CancellationToken ct);

    /// <summary>Saves a new forward draft.</summary>
    Task<Draft> SaveForwardAsync(string chatTitle, PendingForward forward, CancellationToken ct);

    /// <summary>Saves a new reaction draft.</summary>
    Task<Draft> SaveReactionAsync(string chatTitle, PendingReaction reaction, CancellationToken ct);

    /// <summary>All drafts, oldest first.</summary>
    Task<IReadOnlyList<Draft>> ListAsync(CancellationToken ct);

    /// <summary>One draft, or null.</summary>
    Task<Draft?> GetAsync(string id, CancellationToken ct);

    /// <summary>Deletes a draft; false if it did not exist.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct);
}
