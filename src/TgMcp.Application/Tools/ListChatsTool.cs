using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>list_chats: chats from the main list, filtered by limits.listChats.</summary>
public sealed class ListChatsTool
{
    /// <summary>Upper bound for the limit argument.</summary>
    public const int MaxLimit = 500;

    private readonly ToolContext _c;

    /// <summary>Creates the tool.</summary>
    public ListChatsTool(ToolContext context) => _c = context;

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(int limit, CancellationToken ct) =>
        _c.Runner.RunAsync("list_chats", [], async token =>
        {
            var mode = _c.Policy.Config.ListChats;
            if (mode == ListChatsMode.Off)
                return _c.Deny(new Decision(false, "limits", "\"limits.listChats\" is \"off\"", Source: DecisionSource.Limits), null);
            var ids = await _c.Telegram.GetChatIdsAsync(Math.Clamp(limit, 1, MaxLimit), token).ConfigureAwait(false);
            var rows = new List<object>();
            foreach (var id in ids)
            {
                var chat = await _c.Telegram.GetChatAsync(id, token).ConfigureAwait(false);
                if (_c.Policy.Listing(chat.Info) is not Decision d) continue;
                rows.Add(mode == ListChatsMode.AllowedOnly
                    ? new
                    {
                        id = chat.Id, title = chat.Title, type = Names.Snake(chat.Kind), usernames = chat.Usernames,
                        is_forum = chat.IsForum, read = d.Read,
                        features = d.FeatureSet.Select(Names.Snake).Order(StringComparer.Ordinal).ToList(),
                        since_days = d.SinceDays, topics = d.Topics?.Order().ToList(),
                    }
                    : new { id = chat.Id, title = chat.Title, type = Names.Snake(chat.Kind), readable = _c.Policy.Check(chat.Info, Need.Read).Allowed });
            }
            var payload = new { note = ToolResult.ThirdPartyNote, mode = Names.Snake(mode), chats = rows };
            return ToolOutcome.Ok(payload, null, rows.Count, new Decision(true, "limits", "ok"), $"listed {rows.Count} of {ids.Count}");
        }, ct);
}
