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
}

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
            RuleIndex: r?.Index);
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
