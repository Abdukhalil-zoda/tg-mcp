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
/// <param name="Phone">The message to a phone number (kind "phone_message").</param>
/// <param name="AddMember">The user to add to a chat (kind "add_member").</param>
/// <param name="ShareFolder">The chats to share as folder links (kind "share_folder").</param>
/// <param name="JoinFolder">The folder link to join (kind "join_folder").</param>
public sealed record Draft(string Id, DateTimeOffset CreatedUtc, string ChatTitle, OutgoingMessage? Message,
    string Kind = Draft.MessageKind, PendingJoin? Join = null, PendingReaction? Reaction = null, PendingForward? Forward = null,
    PendingEdit? Edit = null, PendingDelete? Delete = null, PendingPhoneMessage? Phone = null,
    PendingAddMember? AddMember = null, PendingShareFolder? ShareFolder = null, PendingJoinFolder? JoinFolder = null)
{
    /// <summary>Kind of an add-member draft.</summary>
    public const string AddMemberKind = "add_member";

    /// <summary>Kind of a share-folder draft.</summary>
    public const string ShareFolderKind = "share_folder";

    /// <summary>Kind of a join-folder draft.</summary>
    public const string JoinFolderKind = "join_folder";

    /// <summary>Kind of a message to a phone number.</summary>
    public const string PhoneKind = "phone_message";

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

/// <summary>A user to add to a group or channel, waiting for the owner's approval.</summary>
/// <param name="ChatId">Target group or channel.</param>
/// <param name="UserId">User to add.</param>
/// <param name="UserLabel">@username or display name at the time (for the owner's overview).</param>
public sealed record PendingAddMember(long ChatId, long UserId, string UserLabel);

/// <summary>Chats to share as one or more folder invite links, waiting for the owner's approval.</summary>
/// <param name="Name">Folder name (1-12 characters).</param>
/// <param name="ChatIds">Chats to put into the folder(s).</param>
public sealed record PendingShareFolder(string Name, IReadOnlyList<long> ChatIds);

/// <summary>A folder link to join, waiting for the owner's approval.</summary>
/// <param name="Slug">The link slug (stored owner-only; never audited).</param>
/// <param name="FolderName">Folder name at preview time.</param>
/// <param name="ChatCount">Chats that would be joined at preview time.</param>
public sealed record PendingJoinFolder(string Slug, string FolderName, int ChatCount)
{
    /// <summary>The link to join.</summary>
    public FolderLink Link => new(Slug);
}

/// <summary>A first message to a phone number, waiting for the owner's approval (the number is stored owner-only, never audited).</summary>
/// <param name="Phone">E.164 number.</param>
/// <param name="ContactName">Name for the imported contact.</param>
/// <param name="Text">Plain text.</param>
/// <param name="DisableNotification">Send silently.</param>
public sealed record PendingPhoneMessage(string Phone, string ContactName, string Text, bool DisableNotification);

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

    /// <summary>Saves a new phone-message draft.</summary>
    Task<Draft> SavePhoneAsync(PendingPhoneMessage message, CancellationToken ct);

    /// <summary>Saves a new edit draft.</summary>
    Task<Draft> SaveEditAsync(string chatTitle, PendingEdit edit, CancellationToken ct);

    /// <summary>Saves a new delete draft.</summary>
    Task<Draft> SaveDeleteAsync(string chatTitle, PendingDelete delete, CancellationToken ct);

    /// <summary>Saves a new forward draft.</summary>
    Task<Draft> SaveForwardAsync(string chatTitle, PendingForward forward, CancellationToken ct);

    /// <summary>Saves a new reaction draft.</summary>
    Task<Draft> SaveReactionAsync(string chatTitle, PendingReaction reaction, CancellationToken ct);

    /// <summary>Saves a new add-member draft.</summary>
    Task<Draft> SaveAddMemberAsync(string chatTitle, PendingAddMember add, CancellationToken ct);

    /// <summary>Saves a new share-folder draft.</summary>
    Task<Draft> SaveShareFolderAsync(PendingShareFolder share, CancellationToken ct);

    /// <summary>Saves a new join-folder draft.</summary>
    Task<Draft> SaveJoinFolderAsync(PendingJoinFolder join, CancellationToken ct);

    /// <summary>All drafts, oldest first.</summary>
    Task<IReadOnlyList<Draft>> ListAsync(CancellationToken ct);

    /// <summary>One draft, or null.</summary>
    Task<Draft?> GetAsync(string id, CancellationToken ct);

    /// <summary>Deletes a draft; false if it did not exist.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct);
}
