using TgMcp.Application.Telegram;

namespace TgMcp.Application.Drafts;

/// <summary>A message the assistant proposed in a chat with <c>"write": "draft"</c>; only the owner can send it.</summary>
/// <param name="Id">Short draft id.</param>
/// <param name="CreatedUtc">When it was saved.</param>
/// <param name="ChatTitle">Chat title at the time (for the owner's overview).</param>
/// <param name="Message">What would be sent.</param>
public sealed record Draft(string Id, DateTimeOffset CreatedUtc, string ChatTitle, OutgoingMessage Message);

/// <summary>Owner-only storage for drafts. Draft text is message content: never log it.</summary>
public interface IDraftStore
{
    /// <summary>Saves a new draft and returns it with its id.</summary>
    Task<Draft> SaveAsync(string chatTitle, OutgoingMessage message, CancellationToken ct);

    /// <summary>All drafts, oldest first.</summary>
    Task<IReadOnlyList<Draft>> ListAsync(CancellationToken ct);

    /// <summary>One draft, or null.</summary>
    Task<Draft?> GetAsync(string id, CancellationToken ct);

    /// <summary>Deletes a draft; false if it did not exist.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct);
}
