namespace TgMcp.Domain;

/// <summary>Write permission of a chat. Write tools are not part of this version; the mode is parsed and reported only.</summary>
public enum WriteMode
{
    /// <summary>No writing.</summary>
    Off,
    /// <summary>Drafts only; the owner sends them.</summary>
    Draft,
    /// <summary>Direct sending.</summary>
    On,
}

/// <summary>What <c>list_chats</c> may reveal.</summary>
public enum ListChatsMode
{
    /// <summary>Only chats that are readable.</summary>
    AllowedOnly,
    /// <summary>Names of all chats except denied ones.</summary>
    NamesAll,
    /// <summary>Listing is refused.</summary>
    Off,
}

/// <summary>Chat kind used by the <c>"type"</c> deny selector.</summary>
public enum ChatKind
{
    /// <summary>Private chat with a person (also secret chats and Saved Messages).</summary>
    Private,
    /// <summary>Basic group or non-channel supergroup.</summary>
    Group,
    /// <summary>Broadcast channel.</summary>
    Channel,
    /// <summary>Private chat with a bot.</summary>
    Bot,
}

/// <summary>Per-chat features (lowercase snake_case names in env lists).</summary>
public enum Feature
{
    /// <summary>search: text search.</summary>
    Search,
    /// <summary>media_download: download attached files.</summary>
    MediaDownload,
    /// <summary>summarize (reserved for a later version).</summary>
    Summarize,
    /// <summary>translate (reserved for a later version).</summary>
    Translate,
    /// <summary>react (reserved for a later version).</summary>
    React,
    /// <summary>mark_read (reserved for a later version).</summary>
    MarkRead,
    /// <summary>edit_own (reserved for a later version).</summary>
    EditOwn,
    /// <summary>delete_own (reserved for a later version).</summary>
    DeleteOwn,
    /// <summary>forward (reserved for a later version).</summary>
    Forward,
    /// <summary>pin (reserved for a later version).</summary>
    Pin,
}

/// <summary>Global switches (<c>features.x</c> in the config file). Read and Send gate reading/writing; the rest gate features.</summary>
public enum GlobalSwitch
{
    /// <summary>Reading messages.</summary>
    Read,
    /// <summary>Search feature.</summary>
    Search,
    /// <summary>Media download feature.</summary>
    MediaDownload,
    /// <summary>Summarize feature.</summary>
    Summarize,
    /// <summary>Translate feature.</summary>
    Translate,
    /// <summary>Writing (drafts or sending).</summary>
    Send,
    /// <summary>React feature.</summary>
    React,
    /// <summary>Mark-read feature.</summary>
    MarkRead,
    /// <summary>Edit-own feature.</summary>
    EditOwn,
    /// <summary>Delete-own feature.</summary>
    DeleteOwn,
    /// <summary>Forward feature.</summary>
    Forward,
    /// <summary>Pin feature.</summary>
    Pin,
    /// <summary>Joining groups/channels (join_chat, preview_chat).</summary>
    Join,
    /// <summary>Searching public chats, channels and users by text (search_public).</summary>
    SearchPublic,
    /// <summary>Adding users to groups/channels (add_member; also needs addMember.mode).</summary>
    AddMember,
    /// <summary>Finding the owner's own groups/channels by title (find_chats; directory data only).</summary>
    FindChats,
    /// <summary>Chat folder links (share_chats_as_folder, join_chat_folder; also needs folders.mode).</summary>
    Folders,
}

/// <summary>What a tool call needs from the policy.</summary>
public enum Need
{
    /// <summary>Read messages.</summary>
    Read,
    /// <summary>Write in any mode (draft or on).</summary>
    Write,
    /// <summary>Write directly (mode on).</summary>
    WriteOn,
    /// <summary>Feature search.</summary>
    Search,
    /// <summary>Feature media_download.</summary>
    MediaDownload,
    /// <summary>Feature summarize.</summary>
    Summarize,
    /// <summary>Feature translate.</summary>
    Translate,
    /// <summary>Feature react.</summary>
    React,
    /// <summary>Feature mark_read.</summary>
    MarkRead,
    /// <summary>Feature edit_own.</summary>
    EditOwn,
    /// <summary>Feature delete_own.</summary>
    DeleteOwn,
    /// <summary>Feature forward.</summary>
    Forward,
    /// <summary>Feature pin.</summary>
    Pin,
    /// <summary>Join a group or channel (global switch features.join + join.mode).</summary>
    Join,
    /// <summary>Search public chats (global switch features.searchPublic).</summary>
    SearchPublic,
    /// <summary>Add a member to a group or channel (global switch features.addMember + addMember.mode).</summary>
    AddMember,
    /// <summary>Find the owner's own groups/channels by title (global switch features.findChats).</summary>
    FindChats,
    /// <summary>Share or join chat folders by invite link (global switch features.folders + folders.mode).</summary>
    Folders,
}

/// <summary>Which part of the policy produced a decision.</summary>
public enum DecisionSource
{
    /// <summary>Master switch, global switches, invalid config or unresolved folders.</summary>
    Global,
    /// <summary>A Deny rule (a deliberate hard block).</summary>
    Deny,
    /// <summary>A Chats rule.</summary>
    ChatRule,
    /// <summary>No Chats rule matched; Defaults applied.</summary>
    Defaults,
    /// <summary>A limit (listing mode, size, rate).</summary>
    Limits,
}

/// <summary>A chat as seen by the policy (no Telegram types).</summary>
/// <param name="Id">Chat id.</param>
/// <param name="Title">Chat title.</param>
/// <param name="Usernames">Active usernames, any case, with or without '@'.</param>
/// <param name="Kind">Chat kind.</param>
public sealed record ChatInfo(long Id, string Title, IReadOnlyList<string> Usernames, ChatKind Kind)
{
    /// <summary>Convenience constructor for a chat with at most one username.</summary>
    public ChatInfo(long id, string title, string? username, ChatKind kind)
        : this(id, title, username is null ? [] : [username], kind)
    {
    }
}

/// <summary>Outcome of a policy check, with the effective permissions of the matched rule.</summary>
/// <param name="Allowed">True if the call may proceed.</param>
/// <param name="Rule">Matched rule label ("global", "defaults", "chats[0] id=...", "deny[1] type=bot").</param>
/// <param name="Reason">Short reason, e.g. "read=false".</param>
/// <param name="Read">Effective read permission.</param>
/// <param name="Write">Effective write mode.</param>
/// <param name="Features">Effective per-chat features.</param>
/// <param name="SinceDays">Messages older than this many days are hidden (null = no limit).</param>
/// <param name="Topics">Allowed forum topic ids (null = all).</param>
/// <param name="Source">Which part of the policy decided.</param>
/// <param name="RuleIndex">N of the matched <c>Chats__N</c> rule, if any.</param>
/// <param name="Missing">The need that was not granted, for denials by <see cref="IPolicy.Check"/>.</param>
/// <param name="Notify">Effective <c>notify</c>: inbound messages of this chat become channel events.</param>
public sealed record Decision(
    bool Allowed,
    string Rule,
    string Reason,
    bool Read = false,
    WriteMode Write = WriteMode.Off,
    IReadOnlySet<Feature>? Features = null,
    int? SinceDays = null,
    IReadOnlySet<int>? Topics = null,
    DecisionSource Source = DecisionSource.Global,
    int? RuleIndex = null,
    Need? Missing = null,
    bool Notify = false)
{
    /// <summary>Effective features, never null.</summary>
    public IReadOnlySet<Feature> FeatureSet => Features ?? Names.NoFeatures;

    /// <summary>Messages strictly older than this must not be returned (null = no limit).</summary>
    public DateTimeOffset? Cutoff(DateTimeOffset now) =>
        SinceDays is int d ? now - TimeSpan.FromDays(d) : null;

    /// <summary>True if a message sent at <paramref name="messageDate"/> is within SinceDays.</summary>
    public bool DateOk(DateTimeOffset messageDate, DateTimeOffset now) =>
        Cutoff(now) is not DateTimeOffset c || messageDate >= c;

    /// <summary>True if a message in this forum topic (null = no topic) may be returned.</summary>
    public bool TopicOk(int? topicId) =>
        Topics is null || (topicId is int t && Topics.Contains(t));

    /// <summary>A copy of this decision turned into a denial.</summary>
    public Decision Deny(string reason) => this with { Allowed = false, Reason = reason };
}

/// <summary>Name mapping between env values and enums.</summary>
public static class Names
{
    /// <summary>The empty feature set.</summary>
    public static readonly IReadOnlySet<Feature> NoFeatures = new HashSet<Feature>();

    /// <summary>Env feature names (snake_case) to features.</summary>
    public static readonly IReadOnlyDictionary<string, Feature> FeatureByName =
        new Dictionary<string, Feature>(StringComparer.Ordinal)
        {
            ["search"] = Feature.Search,
            ["media_download"] = Feature.MediaDownload,
            ["summarize"] = Feature.Summarize,
            ["translate"] = Feature.Translate,
            ["react"] = Feature.React,
            ["mark_read"] = Feature.MarkRead,
            ["edit_own"] = Feature.EditOwn,
            ["delete_own"] = Feature.DeleteOwn,
            ["forward"] = Feature.Forward,
            ["pin"] = Feature.Pin,
        };

    /// <summary>snake_case name of a feature.</summary>
    public static string Snake(Feature f) => FeatureByName.First(kv => kv.Value == f).Key;

    /// <summary>camelCase JSON name of a global switch, e.g. <c>mediaDownload</c>.</summary>
    public static string Camel(GlobalSwitch s)
    {
        var n = s.ToString();
        return char.ToLowerInvariant(n[0]) + n[1..];
    }

    /// <summary>The stricter of two modes: off beats draft, draft beats on.</summary>
    public static WriteMode Stricter(WriteMode a, WriteMode b) => (WriteMode)Math.Min((int)a, (int)b);

    /// <summary>Env name of a write mode.</summary>
    public static string Snake(WriteMode m) => m switch
    {
        WriteMode.Draft => "draft",
        WriteMode.On => "on",
        _ => "off",
    };

    /// <summary>Env name of a chat kind.</summary>
    public static string Snake(ChatKind k) => k switch
    {
        ChatKind.Group => "group",
        ChatKind.Channel => "channel",
        ChatKind.Bot => "bot",
        _ => "private",
    };

    /// <summary>Env name of a listing mode.</summary>
    public static string Snake(ListChatsMode m) => m switch
    {
        ListChatsMode.AllowedOnly => "allowed_only",
        ListChatsMode.NamesAll => "names_all",
        _ => "off",
    };

    /// <summary>The per-chat feature a need requires, or null for read/write needs.</summary>
    public static Feature? AsFeature(Need n) => n switch
    {
        Need.Read or Need.Write or Need.WriteOn or Need.Join or Need.SearchPublic or Need.AddMember or Need.FindChats or Need.Folders => null,
        _ => Enum.Parse<Feature>(n.ToString()),
    };

    /// <summary>The global switch gating a need.</summary>
    public static GlobalSwitch Switch(Need n) => n switch
    {
        Need.Read => GlobalSwitch.Read,
        Need.Write or Need.WriteOn => GlobalSwitch.Send,
        _ => Enum.Parse<GlobalSwitch>(n.ToString()),
    };

    /// <summary>Lowercase username without '@'.</summary>
    public static string NormalizeUsername(string u)
    {
        ArgumentNullException.ThrowIfNull(u);
        return u.Trim().TrimStart('@').ToLowerInvariant();
    }
}
