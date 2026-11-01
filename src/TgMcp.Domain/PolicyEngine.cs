namespace TgMcp.Domain;

/// <summary>Access policy evaluated for every tool call.</summary>
public interface IPolicy
{
    /// <summary>The validated configuration.</summary>
    PolicyConfig Config { get; }

    /// <summary>True if some Deny rule uses a Folder selector (folder membership must be supplied).</summary>
    bool NeedsFolders { get; }

    /// <summary>Permissions for a chat regardless of the request; Allowed=false if hard-denied.</summary>
    Decision Effective(ChatInfo chat);

    /// <summary>Full decision for a call needing <paramref name="needs"/> in <paramref name="chat"/>.</summary>
    Decision Check(ChatInfo chat, params Need[] needs);

    /// <summary>Global (chat-independent) gate: valid config, master switch, global switches, folder resolution.</summary>
    Decision CheckGlobal(params Need[] needs);

    /// <summary>Like <see cref="CheckGlobal"/> but ignores folder resolution (used before contacting Telegram).</summary>
    Decision CheckSwitches(params Need[] needs);

    /// <summary>Decision to show in list_chats, or null if the chat must stay hidden.</summary>
    Decision? Listing(ChatInfo chat);

    /// <summary>Why everything is denied right now, or null.</summary>
    string? GlobalDenyReason();

    /// <summary>True once folder membership was supplied (or no folder deny exists).</summary>
    bool FoldersResolved { get; }

    /// <summary>Supplies folder title to chat ids; folder denies whose folder is missing fail closed.</summary>
    void SetFolders(IReadOnlyDictionary<string, IReadOnlySet<long>> folders);

    /// <summary>Records that folders could not be resolved; folder denies then match every chat.</summary>
    void SetFolderError(string reason);

    /// <summary>
    /// Whether join_chat may act on <paramref name="target"/> now: global gates and features.join, then join.mode
    /// (off: denied; draft: allowed as a draft; on: only if join.allow matches). Deny rules are checked separately
    /// on the resolved chat with <see cref="Effective"/>.
    /// </summary>
    JoinDecision CheckJoin(JoinTarget target);

    /// <summary>
    /// Whether a message may go to a new person by phone: master switch and features.send, then contacts.mode
    /// (off: denied; draft / on: allowed) and the environment-only TGMCP_ContactsAllow list, which is required in every mode.
    /// </summary>
    JoinDecision CheckPhone(string e164);

    /// <summary>
    /// Whether add_member may add someone to <paramref name="chat"/> now: global gates and features.addMember, then
    /// addMember.mode (off: denied; draft: allowed as a draft; on: only if addMember.chats names the chat). Deny rules
    /// on the chat and on the user are checked separately with <see cref="Effective"/>.
    /// </summary>
    JoinDecision CheckAddMember(ChatInfo chat);

    /// <summary>
    /// Whether a chat folder may be shared or joined now: global gates and features.folders, then folders.mode
    /// (off: denied; draft: allowed as a draft; on: allowed). Deny rules on the folder's chats are checked separately.
    /// </summary>
    JoinDecision CheckFolders();
}

/// <summary>Outcome of a join check.</summary>
/// <param name="Allowed">True if the call may proceed (as a draft or a join).</param>
/// <param name="Mode">Effective join.mode.</param>
/// <param name="Reason">Short reason.</param>
/// <param name="Global">The global decision when a global gate denied it (master switch, features.join, config).</param>
public sealed record JoinDecision(bool Allowed, WriteMode Mode, string Reason, Decision? Global = null);

/// <summary>
/// Decision order: master Enabled -> global feature switch -> Deny (always wins) -> first matching Chats
/// rule (else Defaults) -> rule's read/write/features. SinceDays/Topics are applied by the caller through
/// <see cref="Decision.DateOk"/> and <see cref="Decision.TopicOk"/>; limits via <see cref="PolicyConfig"/>.
/// </summary>
public sealed class PolicyEngine : IPolicy
{
    private volatile IReadOnlyDictionary<string, IReadOnlySet<long>>? _folders;
    private volatile string? _folderError = "chat folders not resolved yet";

    /// <summary>Creates an engine for a parsed config.</summary>
    public PolicyEngine(PolicyConfig config) => Config = config ?? throw new ArgumentNullException(nameof(config));

    /// <inheritdoc />
    public PolicyConfig Config { get; }

    /// <inheritdoc />
    public bool NeedsFolders => Config.Denies.Any(d => d.Selector.Kind == SelectorKind.Folder);

    /// <inheritdoc />
    public bool FoldersResolved => !NeedsFolders || _folders is not null;

    /// <inheritdoc />
    public void SetFolders(IReadOnlyDictionary<string, IReadOnlySet<long>> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var missing = Config.Denies.Where(d => d.Selector.Kind == SelectorKind.Folder && !folders.ContainsKey(d.Selector.Value))
            .Select(d => d.Label).ToList();
        _folders = folders;
        _folderError = missing.Count == 0 ? null : $"folder not found: {string.Join(", ", missing)}";
    }

    /// <inheritdoc />
    public void SetFolderError(string reason)
    {
        _folders = null;
        _folderError = reason;
    }

    /// <inheritdoc />
    public string? GlobalDenyReason()
    {
        if (!Config.IsValid) return ConfigProblem();
        if (!Config.Enabled) return "\"enabled\" is not true";
        if (NeedsFolders && _folderError is not null) return $"folder deny unresolved, failing closed: {_folderError}";
        return null;
    }

    /// <inheritdoc />
    public Decision CheckGlobal(params Need[] needs)
    {
        var d = CheckSwitches(needs);
        if (!d.Allowed) return d;
        return GlobalDenyReason() is string why ? new Decision(false, "global", why) : d;
    }

    /// <inheritdoc />
    public Decision CheckSwitches(params Need[] needs)
    {
        ArgumentNullException.ThrowIfNull(needs);
        if (!Config.IsValid) return new Decision(false, "global", ConfigProblem());
        if (!Config.Enabled) return new Decision(false, "global", "\"enabled\" is not true");
        foreach (var n in needs)
        {
            var sw = Names.Switch(n);
            if (!Config.Switches.Contains(sw)) return new Decision(false, "global", $"\"features.{Names.Camel(sw)}\" is off");
        }
        return new Decision(true, "global", "ok");
    }

    /// <inheritdoc />
    public Decision Effective(ChatInfo chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        if (GlobalDenyReason() is string why) return new Decision(false, "global", why);
        foreach (var d in Config.Denies)
            if (Matches(d.Selector, chat)) return new Decision(false, d.Label, "denied by rule", Source: DecisionSource.Deny);
        var r = Config.Chats.FirstOrDefault(r => Matches(r.Selector, chat));
        return new Decision(true, r?.Label ?? "defaults", "ok",
            Read: r?.Read ?? Config.DefaultRead,
            Write: r?.Write ?? Config.DefaultWrite,
            Features: r?.Features ?? Config.DefaultFeatures,
            SinceDays: r?.SinceDays ?? Config.DefaultSinceDays,
            Topics: r?.Topics,
            Source: r is null ? DecisionSource.Defaults : DecisionSource.ChatRule,
            RuleIndex: r?.Index,
            Notify: r?.Notify ?? Config.DefaultNotify);
    }

    /// <inheritdoc />
    public Decision Check(ChatInfo chat, params Need[] needs)
    {
        var g = CheckGlobal(needs);
        if (!g.Allowed) return g;
        var e = Effective(chat);
        if (!e.Allowed) return e;
        foreach (var n in needs)
        {
            string? why = n switch
            {
                Need.Read when !e.Read => "read=false",
                Need.Write when e.Write == WriteMode.Off => "write=off",
                Need.WriteOn when e.Write != WriteMode.On => $"write={Names.Snake(e.Write)} (needs on)",
                _ when Names.AsFeature(n) is Feature f && !e.FeatureSet.Contains(f) => $"feature {Names.Snake(f)} not enabled for this chat",
                _ => null,
            };
            if (why is not null) return e.Deny(why) with { Missing = n };
        }
        return e;
    }

    /// <inheritdoc />
    public Decision? Listing(ChatInfo chat)
    {
        if (Config.ListChats == ListChatsMode.Off) return null;
        var e = Effective(chat);
        if (!e.Allowed) return null;
        if (Config.ListChats == ListChatsMode.AllowedOnly && !Check(chat, Need.Read).Allowed) return null;
        return e;
    }

    /// <inheritdoc />
    public JoinDecision CheckJoin(JoinTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var g = CheckGlobal(Need.Join);
        if (!g.Allowed) return new JoinDecision(false, Config.JoinMode, g.Reason, g);
        return Config.JoinMode switch
        {
            WriteMode.Off => new JoinDecision(false, WriteMode.Off, "join.mode is off"),
            WriteMode.Draft => new JoinDecision(true, WriteMode.Draft, "draft"),
            _ when Config.JoinAllow.Count == 0 => new JoinDecision(false, WriteMode.On, "join.allow is empty"),
            _ when Config.JoinAllow.Any(a => a.Matches(target)) => new JoinDecision(true, WriteMode.On, "allowed by join.allow"),
            _ => new JoinDecision(false, WriteMode.On, "target is not in join.allow"),
        };
    }

    /// <inheritdoc />
    public JoinDecision CheckPhone(string e164)
    {
        ArgumentNullException.ThrowIfNull(e164);
        var g = CheckGlobal(Need.Write);
        if (!g.Allowed) return new JoinDecision(false, Config.ContactsMode, g.Reason, g);
        if (Config.ContactsMode == WriteMode.Off) return new JoinDecision(false, WriteMode.Off, "contacts.mode is off");
        if (Config.ContactsAllow.Count == 0) return new JoinDecision(false, Config.ContactsMode, "TGMCP_ContactsAllow is empty");
        return Config.ContactsAllow.Contains(e164)
            ? new JoinDecision(true, Config.ContactsMode, "allowed by TGMCP_ContactsAllow")
            : new JoinDecision(false, Config.ContactsMode, "number is not in TGMCP_ContactsAllow");
    }

    /// <inheritdoc />
    public JoinDecision CheckAddMember(ChatInfo chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var g = CheckGlobal(Need.AddMember);
        if (!g.Allowed) return new JoinDecision(false, Config.AddMemberMode, g.Reason, g);
        return Config.AddMemberMode switch
        {
            WriteMode.Off => new JoinDecision(false, WriteMode.Off, "addMember.mode is off"),
            WriteMode.Draft => new JoinDecision(true, WriteMode.Draft, "draft"),
            _ when Config.AddMemberChats.Count == 0 => new JoinDecision(false, WriteMode.On, "addMember.chats is empty"),
            _ when Config.AddMemberChats.Any(a => a.Matches(chat)) => new JoinDecision(true, WriteMode.On, "allowed by addMember.chats"),
            _ => new JoinDecision(false, WriteMode.On, "chat is not in addMember.chats"),
        };
    }

    /// <inheritdoc />
    public JoinDecision CheckFolders()
    {
        var g = CheckGlobal(Need.Folders);
        if (!g.Allowed) return new JoinDecision(false, Config.FoldersMode, g.Reason, g);
        return Config.FoldersMode == WriteMode.Off
            ? new JoinDecision(false, WriteMode.Off, "folders.mode is off")
            : new JoinDecision(true, Config.FoldersMode, Names.Snake(Config.FoldersMode));
    }

    private string ConfigProblem() =>
        $"config invalid ({Config.Errors.Count} error(s): {string.Join("; ", Config.Errors.Take(3))})";

    private bool Matches(Selector s, ChatInfo chat) => s.Kind switch
    {
        SelectorKind.Id => s.Id == chat.Id,
        SelectorKind.Username => chat.Usernames.Any(u => Names.NormalizeUsername(u) == s.Value),
        SelectorKind.Title => s.Value == chat.Title,
        SelectorKind.Type => s.Type == chat.Kind,
        // Fail closed: an unresolved folder matches every chat.
        SelectorKind.Folder => _folders is not { } f || !f.TryGetValue(s.Value, out var ids) || ids.Contains(chat.Id),
        _ => true,
    };
}
