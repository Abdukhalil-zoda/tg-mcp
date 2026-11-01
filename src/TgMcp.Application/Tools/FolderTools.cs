using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>Shared chat-folder logic: denials, Telegram limit texts, splitting, the share and join steps.</summary>
internal static class FolderSupport
{
    /// <summary>Most chats per shared folder (Telegram's limit without Premium).</summary>
    public const int ChatsPerFolder = 100;

    /// <summary>Most chat ids per share_chats_as_folder call (10 folders of 100).</summary>
    public const int MaxChats = 1000;

    /// <summary>Telegram's limit for folder names.</summary>
    public const int MaxNameLength = 12;

    /// <summary>Why an id was left out of a folder.</summary>
    public const string NotEligible = "Telegram does not allow it in a folder link: you must be an admin with the invite-users right, or the chat must be public";

    /// <summary>The folder link, or an <see cref="ArgumentException"/> that never contains the input.</summary>
    public static FolderLink Parse(string link) =>
        FolderLink.TryParse(link, out var l, out var error) ? l! : throw new ArgumentException(error, nameof(link));

    /// <summary>Client-facing text for a denied folder call.</summary>
    public static string Denied(JoinDecision d, PolicyConfig config) => d switch
    {
        { Global: { } g } => Denials.Message(config, g, null),
        _ => "Chat folder links are off. To let the assistant propose folder shares and joins that you approve, add: "
            + "\"features\": { \"folders\": true } and \"folders\": { \"mode\": \"draft\" }",
    };

    /// <summary>Audit-safe reference of a created link (never the link itself).</summary>
    public static string AuditRef(string link) =>
        FolderLink.TryParse(link, out var l, out _) ? l!.AuditRef : "addlist#" + JoinTarget.HashPrefix(link);

    /// <summary>A friendly text for Telegram's folder limits and link errors, or null.</summary>
    public static string? Friendly(TelegramCallException e)
    {
        var m = e.TdMessage;
        bool Has(string s) => m.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("CHATLISTS_TOO_MUCH") || Has("shareable") && (Has("too many") || Has("maximum")))
            return "Telegram limit (CHATLISTS_TOO_MUCH): too many shared folders. Without Premium an account can have only a few shareable folders "
                + "and can join only about 2 shared folders. Remove a shared folder in Telegram first.";
        if (Has("FILTERS_TOO_MUCH") || Has("chat folders") && (Has("too many") || Has("maximum")))
            return "Telegram limit (DIALOG_FILTERS_TOO_MUCH): too many chat folders (10 without Premium). Delete a folder in Telegram first.";
        if (Has("INCLUDE_TOO_MUCH") || Has("too many chats") || Has("pinned and included"))
            return "Telegram limit (FILTER_INCLUDE_TOO_MUCH): too many chats in one folder (100 without Premium).";
        if (Has("INVITES_TOO_MUCH")) return "Telegram limit (INVITES_TOO_MUCH): this folder has too many invite links.";
        if (Has("INVITE_SLUG_EXPIRED") || Has("INVITE_SLUG_EMPTY") || Has("INVITE_SLUG_INVALID"))
            return "Telegram refused: the folder link is invalid or expired.";
        if (Has("FILTER_TITLE_EMPTY")) return "Telegram refused: the folder name is empty.";
        if (Has("PEER_FLOOD"))
            return "Telegram refused (PEER_FLOOD): the account is temporarily limited. Stop for at least a day before trying again.";
        return null;
    }

    /// <summary>Short Telegram error name for the audit log.</summary>
    public static string ErrorName(TelegramCallException e) => AddMemberSupport.ErrorName(e);

    /// <summary>Folder name for part <paramref name="part"/> of <paramref name="parts"/> (fits 12 characters).</summary>
    public static string PartName(string name, int part, int parts)
    {
        if (parts <= 1) return name;
        var suffix = $" {part}";
        var room = MaxNameLength - suffix.Length;
        return (name.Length > room ? name[..room].TrimEnd() : name) + suffix;
    }

    /// <summary>Validated, trimmed folder name.</summary>
    public static string CheckName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length is 0 or > MaxNameLength)
            throw new ArgumentException($"folder name must be 1-{MaxNameLength} characters (Telegram's limit)", nameof(name));
        return n;
    }

    /// <summary>Validated distinct chat ids.</summary>
    public static IReadOnlyList<long> CheckIds(IReadOnlyList<long>? chatIds)
    {
        var ids = (chatIds ?? []).Distinct().ToList();
        if (ids.Count == 0) throw new ArgumentException("chat_ids is empty", nameof(chatIds));
        if (ids.Count > MaxChats) throw new ArgumentException($"at most {MaxChats} chats per call (10 folders of {ChatsPerFolder})", nameof(chatIds));
        if (ids.Contains(0)) throw new ArgumentException("chat ids must be non-zero", nameof(chatIds));
        return ids;
    }

    /// <summary>Splits the ids into candidates (groups/channels not hit by a deny rule) and excluded entries with a reason.</summary>
    public static async Task<(List<TgChat> Candidates, List<object> Excluded)> ClassifyAsync(ToolContext c, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var candidates = new List<TgChat>();
        var excluded = new List<object>();
        foreach (var id in ids)
        {
            TgChat chat;
            try
            {
                chat = await c.Telegram.GetChatAsync(id, ct).ConfigureAwait(false);
            }
            catch (TelegramCallException)
            {
                excluded.Add(new { id, reason = "unknown chat id (not in the owner's chat list)" });
                continue;
            }
            if (c.Policy.Effective(chat.Info).Source == DecisionSource.Deny) excluded.Add(new { id, reason = Denials.HardDeny });
            else if (chat.Kind is not (ChatKind.Group or ChatKind.Channel)) excluded.Add(new { id, title = chat.Title, reason = "people and bots cannot be shared in a folder link" });
            else candidates.Add(chat);
        }
        return (candidates, excluded);
    }

    /// <summary>
    /// Creates one folder and link per chunk of at most 100 candidates. A Telegram refusal stops the remaining chunks
    /// (they are reported as not processed); folders without eligible chats or whose link failed are deleted again.
    /// </summary>
    public static async Task<ToolOutcome> ShareAsync(ToolContext c, string name, IReadOnlyList<long> ids, string rule, string auditPrefix, CancellationToken ct)
    {
        var (candidates, excluded) = await ClassifyAsync(c, ids, ct).ConfigureAwait(false);
        var chunks = candidates.Chunk(ChatsPerFolder).ToList();
        var folders = new List<object>();
        var refs = new List<string>();
        string? error = null;
        var notProcessed = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (error is not null) { notProcessed += chunk.Length; continue; }
            var folderName = PartName(name, i + 1, chunks.Count);
            int folderId;
            try
            {
                folderId = await c.Telegram.CreateChatFolderAsync(folderName, [.. chunk.Select(x => x.Id)], ct).ConfigureAwait(false);
            }
            catch (TelegramCallException e)
            {
                error = Friendly(e) ?? $"Telegram refused to create the folder: {ErrorName(e)}";
                notProcessed += chunk.Length;
                continue;
            }
            try
            {
                var eligible = (await c.Telegram.GetChatsForFolderLinkAsync(folderId, ct).ConfigureAwait(false)).ToHashSet();
                var inLink = chunk.Where(x => eligible.Contains(x.Id)).ToList();
                foreach (var x in chunk.Where(x => !eligible.Contains(x.Id))) excluded.Add(new { id = x.Id, title = x.Title, reason = NotEligible });
                if (inLink.Count == 0)
                {
                    await c.Telegram.DeleteChatFolderAsync(folderId, ct).ConfigureAwait(false);
                    continue;
                }
                var link = await c.Telegram.CreateFolderLinkAsync(folderId, [.. inLink.Select(x => x.Id)], ct).ConfigureAwait(false);
                refs.Add(AuditRef(link));
                folders.Add(new
                {
                    name = folderName,
                    folder_id = folderId,
                    invite_link = link,
                    included = inLink.Select(x => new { id = x.Id, title = x.Title }).ToList(),
                });
            }
            catch (TelegramCallException e)
            {
                error = Friendly(e) ?? $"Telegram refused to create the folder link: {ErrorName(e)}";
                notProcessed += chunk.Length;
                try
                {
                    await c.Telegram.DeleteChatFolderAsync(folderId, ct).ConfigureAwait(false);
                }
                catch (TelegramCallException)
                {
                    // best effort: the empty folder stays and the owner can delete it in Telegram
                }
            }
        }
        var payload = new
        {
            status = folders.Count > 0 ? "shared" : "nothing_shared",
            folders,
            excluded,
            not_processed = notProcessed,
            error,
            note = ToolResult.ThirdPartyNote,
            hint = "Anyone with an invite_link can join these chats: share it only with the intended people.",
        };
        var reason = $"{auditPrefix}{ids.Count} chat(s), {folders.Count} link(s){(refs.Count > 0 ? " " + string.Join(",", refs) : "")}, {excluded.Count} excluded"
            + (error is null ? "" : ", stopped by telegram limit");
        var result = folders.Count == 0 && error is not null ? ToolResult.Error(error + $" ({excluded.Count} chat(s) excluded, {notProcessed} not processed)") : ToolResult.Ok(payload);
        return new ToolOutcome(result, null, folders.Count, true, rule, reason);
    }

    /// <summary>Preview of a folder link: the folder name and its chats split into joinable ones and those hidden by deny rules.</summary>
    public static async Task<(TgFolderPreview Preview, List<TgChat> ToJoin, int Denied)> PreviewAsync(ToolContext c, FolderLink link, CancellationToken ct)
    {
        TgFolderPreview preview;
        try
        {
            preview = await c.Telegram.CheckFolderLinkAsync(link, ct).ConfigureAwait(false);
        }
        catch (TelegramCallException e) when (Friendly(e) is { } friendly)
        {
            throw new TelegramCallException(e.Code, friendly);
        }
        var toJoin = new List<TgChat>();
        var denied = 0;
        foreach (var id in preview.MissingChatIds)
        {
            TgChat chat;
            try
            {
                chat = await c.Telegram.GetChatAsync(id, ct).ConfigureAwait(false);
            }
            catch (TelegramCallException)
            {
                denied++; // cannot be checked against deny rules: fail closed
                continue;
            }
            if (c.Policy.Effective(chat.Info).Source == DecisionSource.Deny) denied++;
            else toJoin.Add(chat);
        }
        return (preview, toJoin, denied);
    }

    /// <summary>Preview payload fields shared by preview, draft and join results.</summary>
    public static object Chats(IEnumerable<TgChat> chats) =>
        chats.Select(x => new { id = x.Id, title = x.Title, type = Names.Snake(x.Kind) }).ToList();

    /// <summary>Adds the folder and joins its chats (minus denied ones).</summary>
    public static async Task<ToolOutcome> JoinAsync(ToolContext c, FolderLink link, WriteMode mode, string rule, string auditPrefix, CancellationToken ct)
    {
        var (preview, toJoin, denied) = await PreviewAsync(c, link, ct).ConfigureAwait(false);
        var reason = $"{link.AuditRef}, mode {Names.Snake(mode)}, ";
        if (toJoin.Count == 0)
        {
            var nothing = new { status = preview.AlreadyAdded ? "already_added" : "nothing_to_join", folder = preview.Name, excluded_by_policy = denied, note = ToolResult.ThirdPartyNote };
            return new ToolOutcome(ToolResult.Ok(nothing), null, 0, true, rule, reason + auditPrefix + "nothing to join");
        }
        try
        {
            await c.Telegram.AddFolderByLinkAsync(link, [.. toJoin.Select(x => x.Id)], ct).ConfigureAwait(false);
        }
        catch (TelegramCallException e) when (Friendly(e) is { } friendly)
        {
            return new ToolOutcome(ToolResult.Error(friendly), null, 0, true, "telegram", reason + auditPrefix + "telegram error " + ErrorName(e));
        }
        var payload = new
        {
            status = "joined",
            folder = preview.Name,
            joined = Chats(toJoin),
            excluded_by_policy = denied,
            access = "Joining grants no read or write access; the chats fall under \"defaults\".",
            note = ToolResult.ThirdPartyNote,
        };
        return new ToolOutcome(ToolResult.Ok(payload), null, toJoin.Count, true, rule, reason + auditPrefix + $"joined {toJoin.Count} chat(s), {denied} excluded");
    }
}

/// <summary>
/// share_chats_as_folder: creates chat folders (at most 100 chats each) from chat ids and a shareable invite link for
/// each. Needs features.folders and folders.mode: off = denied, draft = saved for <c>tgmcp approve</c>, on = created at
/// once. Deny-rule chats, people and bots are excluded; Telegram decides which chats are eligible for a link.
/// </summary>
public sealed class ShareFolderTool
{
    private readonly ToolContext _c;
    private readonly IDraftStore _drafts;

    /// <summary>Creates the tool.</summary>
    public ShareFolderTool(ToolContext context, IDraftStore drafts)
    {
        _c = context;
        _drafts = drafts;
    }

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string name, IReadOnlyList<long> chatIds, CancellationToken ct) =>
        _c.Runner.RunAsync("share_chats_as_folder", [Need.Folders], async token =>
        {
            var n = FolderSupport.CheckName(name);
            var ids = FolderSupport.CheckIds(chatIds);
            var fd = _c.Policy.CheckFolders();
            if (!fd.Allowed)
            {
                return new ToolOutcome(ToolResult.Error(FolderSupport.Denied(fd, _c.Policy.Config)), null, 0, false, "folders",
                    $"{ids.Count} chat(s), mode {Names.Snake(fd.Mode)}, denied: {fd.Reason}");
            }
            if (fd.Mode == WriteMode.Draft)
            {
                var (candidates, excluded) = await FolderSupport.ClassifyAsync(_c, ids, token).ConfigureAwait(false);
                if (candidates.Count == 0)
                    return ToolOutcome.Failed($"none of the {ids.Count} chat(s) can be shared in a folder link (people, bots, unknown or denied chats)", null, "folders");
                var draft = await _drafts.SaveShareFolderAsync(new PendingShareFolder(n, [.. candidates.Select(x => x.Id)]), token).ConfigureAwait(false);
                var folders = (candidates.Count + FolderSupport.ChatsPerFolder - 1) / FolderSupport.ChatsPerFolder;
                var payload = new
                {
                    status = "draft",
                    draft_id = draft.Id,
                    name = n,
                    folders,
                    candidates = FolderSupport.Chats(candidates),
                    excluded,
                    message = $"saved as draft {draft.Id}; the owner creates the folder link(s) with `tgmcp approve {draft.Id}` (nothing was created). "
                        + "Telegram may still leave out chats where the owner has no invite rights.",
                    note = ToolResult.ThirdPartyNote,
                };
                return new ToolOutcome(ToolResult.Ok(payload), null, 1, true, "folders",
                    $"{ids.Count} chat(s), mode draft, draft {draft.Id} saved, {excluded.Count} excluded");
            }
            return await FolderSupport.ShareAsync(_c, n, ids, "folders", "mode on, ", token).ConfigureAwait(false);
        }, ct);
}

/// <summary>
/// join_chat_folder: previews a chat folder link (checkChatFolderInviteLink) and adds the folder, joining all its chats
/// (addChatFolderByInviteLink). Needs features.folders; joining needs folders.mode draft or on. Deny-rule chats are
/// left out. Counts as one join against limits.joinsPerHour / joinsPerDay. The link is never audited.
/// </summary>
public sealed class JoinFolderTool
{
    private readonly ToolContext _c;
    private readonly IDraftStore _drafts;
    private readonly IJoinLimiter _limiter;

    /// <summary>Creates the tool.</summary>
    public JoinFolderTool(ToolContext context, IDraftStore drafts, IJoinLimiter limiter)
    {
        _c = context;
        _drafts = drafts;
        _limiter = limiter;
    }

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string link, bool previewOnly, CancellationToken ct) =>
        _c.Runner.RunAsync("join_chat_folder", [Need.Folders], async token =>
        {
            var l = FolderSupport.Parse(link);
            var config = _c.Policy.Config;
            var fd = _c.Policy.CheckFolders();
            var reason = $"{l.AuditRef}, mode {Names.Snake(fd.Mode)}, ";
            if (!previewOnly && !fd.Allowed)
                return new ToolOutcome(ToolResult.Error(FolderSupport.Denied(fd, config)), null, 0, false, "folders", reason + "denied: " + fd.Reason);
            var (preview, toJoin, denied) = await FolderSupport.PreviewAsync(_c, l, token).ConfigureAwait(false);
            if (previewOnly)
            {
                var p = new
                {
                    status = "preview",
                    folder = preview.Name,
                    already_added = preview.AlreadyAdded,
                    would_join = FolderSupport.Chats(toJoin),
                    already_in = preview.AddedChatIds.Count,
                    excluded_by_policy = denied,
                    folders_mode = Names.Snake(config.FoldersMode),
                    note = ToolResult.ThirdPartyNote,
                };
                return new ToolOutcome(ToolResult.Ok(p), null, toJoin.Count, true, "folders", reason + "previewed");
            }
            if (toJoin.Count == 0)
            {
                var nothing = new { status = preview.AlreadyAdded ? "already_added" : "nothing_to_join", folder = preview.Name, excluded_by_policy = denied, note = ToolResult.ThirdPartyNote };
                return new ToolOutcome(ToolResult.Ok(nothing), null, 0, true, "folders", reason + "nothing to join");
            }
            if (_limiter.TryAcquire() is not PaceLimit.None and var hit)
                return ToolOutcome.Refused(Pace.Message("join", hit, "limits.joinsPerHour", config.JoinsPerHour, "limits.joinsPerDay", config.JoinsPerDay), null, "limits");
            if (fd.Mode == WriteMode.Draft)
            {
                var draft = await _drafts.SaveJoinFolderAsync(new PendingJoinFolder(l.Slug, preview.Name, toJoin.Count), token).ConfigureAwait(false);
                var payload = new
                {
                    status = "draft",
                    draft_id = draft.Id,
                    folder = preview.Name,
                    would_join = FolderSupport.Chats(toJoin),
                    excluded_by_policy = denied,
                    message = $"saved as draft {draft.Id}; the owner joins with `tgmcp approve {draft.Id}` (nothing was joined). "
                        + "Without Premium an account can join only about 2 shared folders.",
                    note = ToolResult.ThirdPartyNote,
                };
                return new ToolOutcome(ToolResult.Ok(payload), null, 1, true, "folders", reason + $"draft {draft.Id} saved, {toJoin.Count} chat(s)");
            }
            return await FolderSupport.JoinAsync(_c, l, fd.Mode, "folders", "", token).ConfigureAwait(false);
        }, ct);
}
