using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TgMcp.Domain;

/// <summary>The single selector of a Chats or Deny rule.</summary>
public enum SelectorKind
{
    /// <summary>Chat id.</summary>
    Id,
    /// <summary>Public username (case-insensitive, '@' optional).</summary>
    Username,
    /// <summary>Exact chat title (Chats rules only).</summary>
    Title,
    /// <summary>Chat kind (Deny rules only).</summary>
    Type,
    /// <summary>Chat folder title (Deny rules only).</summary>
    Folder,
}

/// <summary>A parsed selector.</summary>
/// <param name="Kind">Selector kind.</param>
/// <param name="Value">Normalized text value.</param>
/// <param name="Id">Chat id for <see cref="SelectorKind.Id"/>.</param>
/// <param name="Type">Chat kind for <see cref="SelectorKind.Type"/>.</param>
public sealed record Selector(SelectorKind Kind, string Value, long Id = 0, ChatKind Type = ChatKind.Private)
{
    /// <summary>Label form, e.g. <c>id=-100123</c>.</summary>
    public override string ToString() => Kind switch
    {
        SelectorKind.Id => $"id={Id}",
        SelectorKind.Type => $"type={Names.Snake(Type)}",
        _ => $"{Kind.ToString().ToLowerInvariant()}=\"{Value}\"",
    };
}

/// <summary>Where a configuration value came from.</summary>
public enum ConfigOrigin
{
    /// <summary>Built-in default.</summary>
    Default,
    /// <summary>The JSON config file.</summary>
    File,
    /// <summary>A TGMCP_ environment variable (overrides the file).</summary>
    Env,
}

/// <summary>One flat configuration value.</summary>
/// <param name="Key">IConfiguration-style key, ':' separated, original casing (e.g. <c>chats:0:id</c>).</param>
/// <param name="Value">Raw value; null entries (sections, JSON null) are ignored.</param>
/// <param name="Origin">File or environment.</param>
public sealed record ConfigEntry(string Key, string? Value, ConfigOrigin Origin);

/// <summary>A <c>chats[N]</c> rule; null permissions fall back to Defaults.</summary>
/// <param name="Index">N.</param>
/// <param name="Selector">The rule's selector.</param>
/// <param name="Read">Read permission.</param>
/// <param name="Write">Write mode.</param>
/// <param name="Features">Per-chat features.</param>
/// <param name="SinceDays">Maximum message age in days.</param>
/// <param name="Topics">Allowed forum topic ids (null or empty = all topics).</param>
/// <param name="Name">Optional human label (<c>name</c> or <c>note</c>); never used for matching.</param>
/// <param name="Notify">Inbound messages become channel events (needs read).</param>
public sealed record ChatRule(
    int Index,
    Selector Selector,
    bool? Read,
    WriteMode? Write,
    IReadOnlySet<Feature>? Features,
    int? SinceDays,
    IReadOnlySet<int>? Topics,
    string? Name = null,
    bool? Notify = null)
{
    /// <summary>Label used in decisions and audit.</summary>
    public string Label => $"chats[{Index}] {Selector}";
}

/// <summary>A <c>deny[N]</c> rule. Deny always wins.</summary>
/// <param name="Index">N.</param>
/// <param name="Selector">The rule's selector.</param>
/// <param name="Name">Optional human label; never used for matching.</param>
public sealed record DenyRule(int Index, Selector Selector, string? Name = null)
{
    /// <summary>Label used in decisions and audit.</summary>
    public string Label => $"deny[{Index}] {Selector}";
}

/// <summary>
/// The effective configuration, merged from the JSON config file and TGMCP_ environment variables
/// (precedence: defaults &lt; file &lt; env). Keys are IConfiguration-style (':' separated).
/// Any error makes the whole config invalid => everything is denied.
/// </summary>
public sealed class PolicyConfig
{
    /// <summary>Environment variable prefix.</summary>
    public const string Prefix = "TGMCP_";

    /// <summary>Non-policy keys the process itself reads (environment only). Their values are never logged or hashed.</summary>
    public static readonly IReadOnlySet<string> RuntimeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ApiId", "ApiHash", "DatabaseKey", "DatabaseKeyFile", "AuditPath", "DataDir", "ConfigFile", "UseTestDc",
    };

    /// <summary>Policy keys that may only come from the environment (sensitive values, redacted in reports).</summary>
    public static readonly IReadOnlySet<string> EnvOnlyPolicyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ContactsAllow",
    };

    /// <summary>Secrets: refused in the config file.</summary>
    public static readonly IReadOnlySet<string> SecretKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ApiId", "ApiHash", "DatabaseKey",
    };

    /// <summary>Master switch (default false = deny all).</summary>
    public bool Enabled { get; private set; }
    /// <summary>Global switches that are on.</summary>
    public IReadOnlySet<GlobalSwitch> Switches => _switches;
    /// <summary>defaults.read.</summary>
    public bool DefaultRead { get; private set; }
    /// <summary>defaults.write.</summary>
    public WriteMode DefaultWrite { get; private set; } = WriteMode.Off;
    /// <summary>defaults.features.</summary>
    public IReadOnlySet<Feature> DefaultFeatures { get; private set; } = Names.NoFeatures;
    /// <summary>defaults.notify.</summary>
    public bool DefaultNotify { get; private set; }
    /// <summary>events.enabled: push inbound messages to the MCP client as channel events.</summary>
    public bool EventsEnabled { get; private set; }
    /// <summary>events.includeOutgoing: also emit the owner's own messages.</summary>
    public bool EventsIncludeOutgoing { get; private set; }
    /// <summary>events.maxPerMinute.</summary>
    public int EventsMaxPerMinute { get; private set; } = 30;
    /// <summary>events.maxTextChars: event text is truncated to this length.</summary>
    public int EventsMaxTextChars { get; private set; } = 4000;
    /// <summary>events.catchUpMinutes: on start, emit messages missed in this window (0 = off).</summary>
    public int EventsCatchUpMinutes { get; private set; }
    /// <summary>defaults.sinceDays.</summary>
    public int? DefaultSinceDays { get; private set; }
    /// <summary>Chats rules ordered by N.</summary>
    public IReadOnlyList<ChatRule> Chats { get; private set; } = [];
    /// <summary>Deny rules ordered by N.</summary>
    public IReadOnlyList<DenyRule> Denies { get; private set; } = [];
    /// <summary>limits.listChats.</summary>
    public ListChatsMode ListChats { get; private set; } = ListChatsMode.AllowedOnly;
    /// <summary>limits.maxMessagesPerCall.</summary>
    public int MaxMessagesPerCall { get; private set; } = 200;
    /// <summary>limits.ratePerMinute.</summary>
    public int RatePerMinute { get; private set; } = 30;
    /// <summary>limits.maxMediaMb.</summary>
    public int MaxMediaMb { get; private set; } = 50;

    /// <summary>limits.mediaRetentionDays: downloaded media older than this is deleted (0 = keep forever).</summary>
    public int MediaRetentionDays { get; private set; } = 7;

    /// <summary>contacts.mode: messaging people by phone number. off (default) | draft (owner approves) | on.</summary>
    public WriteMode ContactsMode { get; private set; } = WriteMode.Off;

    /// <summary>TGMCP_ContactsAllow (environment only): E.164 numbers that may be messaged by phone.</summary>
    public IReadOnlyList<string> ContactsAllow { get; private set; } = [];

    /// <summary>limits.newChatsPerDay: messages to new people by phone (sent or drafted) per 24 hours.</summary>
    public int NewChatsPerDay { get; private set; } = 3;

    /// <summary>limits.joinsPerHour: join_chat calls (joined or drafted) per hour.</summary>
    public int JoinsPerHour { get; private set; } = 5;

    /// <summary>join.mode: off (default), draft (owner approves) or on (only targets in join.allow).</summary>
    public WriteMode JoinMode { get; private set; } = WriteMode.Off;

    /// <summary>delete.mode: "draft" (default; every delete needs <c>tgmcp approve</c>) or "on" (delete at once in chats with "write": "on").</summary>
    public WriteMode DeleteMode { get; private set; } = WriteMode.Draft;

    /// <summary>join.allow: targets that may be joined in mode "on" ("*" = any).</summary>
    public IReadOnlyList<JoinAllowEntry> JoinAllow { get; private set; } = [];

    /// <summary>limits.sendsPerMinute: messages (sent or drafted) per chat per minute.</summary>
    public int SendsPerMinute { get; private set; } = 10;

    /// <summary>media.dir: directory for downloaded media (null = {DataDir}/media).</summary>
    public string? MediaDir { get; private set; }

    /// <summary>media.hostDir: host path of the media directory, for reporting only.</summary>
    public string? MediaHostDir { get; private set; }

    /// <summary>Validation errors (key names, never secret values).</summary>
    public IReadOnlyList<string> Errors => _errors;
    /// <summary>True if there are no errors.</summary>
    public bool IsValid => _errors.Count == 0;
    /// <summary>sha256 of the normalized effective config (or of the raw policy keys if invalid).</summary>
    public string Sha256 { get; private set; } = "";

    private readonly HashSet<GlobalSwitch> _switches = [];
    private readonly List<string> _errors = [];
    private readonly Dictionary<string, ConfigEntry> _effective = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConfigEntry> _all = [];

    private PolicyConfig() { }

    /// <summary>Entries from a raw environment (e.g. Environment.GetEnvironmentVariables()); keys without the prefix are ignored.</summary>
    public static IReadOnlyList<ConfigEntry> EnvironmentEntries(IDictionary env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var list = new List<ConfigEntry>();
        foreach (DictionaryEntry e in env)
        {
            var k = e.Key?.ToString() ?? "";
            if (!k.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new(k[Prefix.Length..].Replace("__", ":", StringComparison.Ordinal), e.Value?.ToString(), ConfigOrigin.Env));
        }
        return list;
    }

    /// <summary>Environment-only config (no file).</summary>
    public static PolicyConfig FromEnvironment(IDictionary env) => Parse(EnvironmentEntries(env));

    /// <summary>Test/CLI helper: (key, value) pairs in env form (<c>TGMCP_</c> prefix optional, <c>__</c> nesting).</summary>
    public static PolicyConfig FromPairs(params (string Key, string Value)[] pairs) =>
        Parse(pairs is null ? [] : pairs.Select(p => new ConfigEntry(
            (p.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? p.Key[Prefix.Length..] : p.Key).Replace("__", ":", StringComparison.Ordinal),
            p.Value, ConfigOrigin.Env)));

    /// <summary>Entries as produced by IConfiguration.AsEnumerable(), all treated as environment values.</summary>
    public static PolicyConfig Parse(IEnumerable<KeyValuePair<string, string?>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Parse(entries.Select(e => new ConfigEntry(e.Key, e.Value, ConfigOrigin.Env)));
    }

    /// <summary>
    /// Merges file and environment entries (env overrides a file key and everything below it) and validates.
    /// <paramref name="loadError"/> (e.g. "config file not found") makes the config invalid.
    /// </summary>
    public static PolicyConfig Parse(IEnumerable<ConfigEntry> entries, string? loadError = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var c = new PolicyConfig();
        if (loadError is not null) c._errors.Add(loadError);
        var ordered = entries.Where(e => e.Value is not null).OrderBy(e => e.Origin == ConfigOrigin.Env ? 1 : 0).ToList();
        c._all.AddRange(ordered);
        foreach (var e in ordered)
        {
            var segments = e.Key.Split(':');
            if (e.Origin == ConfigOrigin.File && segments.Length == 1 && (RuntimeKeys.Contains(segments[0]) || EnvOnlyPolicyKeys.Contains(segments[0])))
            {
                var name = RuntimeKeys.Concat(EnvOnlyPolicyKeys).First(k => k.Equals(segments[0], StringComparison.OrdinalIgnoreCase));
                c._errors.Add(SecretKeys.Contains(name)
                    ? $"{e.Key}: secrets are not allowed in the config file; set {Prefix}{name} in the environment instead"
                    : $"{e.Key}: only allowed as the environment variable {Prefix}{name}");
                continue;
            }
            if (c._effective.TryGetValue(e.Key, out var prev) && prev.Origin == e.Origin)
            {
                c._errors.Add($"{e.Key}: duplicate key (differs only in case?)");
                continue;
            }
            if (e.Origin == ConfigOrigin.Env)
            {
                var below = e.Key + ":";
                foreach (var k in c._effective.Keys.Where(k => k.StartsWith(below, StringComparison.OrdinalIgnoreCase)).ToList())
                    c._effective.Remove(k);
            }
            c._effective[e.Key] = e;
        }
        var kv = CollapseLists(c._effective.Values);
        c.Load(kv);
        for (var i = 0; i < c._errors.Count; i++) c._errors[i] = c.Pretty(c._errors[i]);
        c.Sha256 = c.ComputeSha(kv);
        return c;
    }

    /// <summary>JSON arrays of scalars (<c>features:0</c>, <c>topics:1</c>) become one comma-separated value.</summary>
    private static Dictionary<string, string> CollapseLists(IEnumerable<ConfigEntry> entries)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            var i = e.Key.LastIndexOf(':');
            if (i > 0 && int.TryParse(e.Key[(i + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var idx))
            {
                var parent = e.Key[..i];
                var leaf = parent[(parent.LastIndexOf(':') + 1)..];
                if (leaf.Equals("features", StringComparison.OrdinalIgnoreCase) || leaf.Equals("topics", StringComparison.OrdinalIgnoreCase)
                    || leaf.Equals("allow", StringComparison.OrdinalIgnoreCase))
                {
                    if (!lists.TryGetValue(parent, out var items)) lists[parent] = items = [];
                    items[idx] = e.Value!;
                    continue;
                }
            }
            kv[e.Key] = e.Value!;
        }
        foreach (var (parent, items) in lists) kv[parent] = string.Join(",", items.Values);
        return kv;
    }

    /// <summary>Where the value at <paramref name="key"/> (or any value below it) came from: "file", "env", "file+env" or "default".</summary>
    public string SourceOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var below = key + ":";
        var origins = _effective.Values
            .Where(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || e.Key.StartsWith(below, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Origin).Distinct().ToList();
        return origins.Count switch
        {
            0 => "default",
            1 => origins[0] == ConfigOrigin.File ? "file" : "env",
            _ => "file+env",
        };
    }

    /// <summary>
    /// How the owner wrote <paramref name="key"/>: a JSON path (<c>chats[2].reed</c>) for file values,
    /// the variable name (<c>TGMCP_Chats__2__Reed</c>) for environment values.
    /// </summary>
    public string Display(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var below = key + ":";
        var hit = _effective.Values.Concat(_all)
            .Where(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || e.Key.StartsWith(below, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Key.Length == key.Length ? 0 : 1)
            .FirstOrDefault();
        return hit is null ? Render(key, ConfigOrigin.Env) : Render(hit.Key[..key.Length], hit.Origin);
    }

    private static string Render(string key, ConfigOrigin origin)
    {
        if (origin != ConfigOrigin.File) return Prefix + key.Replace(":", "__", StringComparison.Ordinal);
        var sb = new StringBuilder();
        foreach (var seg in key.Split(':'))
        {
            if (seg.Length > 0 && seg.All(char.IsAsciiDigit)) sb.Append('[').Append(seg).Append(']');
            else sb.Append(sb.Length == 0 ? "" : ".").Append(seg);
        }
        return sb.ToString();
    }

    /// <summary>Rewrites the leading key of an error ("chats:0:reed: msg") to how the owner wrote it.</summary>
    private string Pretty(string error)
    {
        var i = error.IndexOf(": ", StringComparison.Ordinal);
        if (i <= 0) return error;
        var key = error[..i];
        return key.Any(char.IsWhiteSpace) || key.Contains('/', StringComparison.Ordinal) ? error : Display(key) + error[i..];
    }

    /// <summary>A config that denies everything (e.g. loading failed).</summary>
    public static PolicyConfig Invalid(string reason)
    {
        var c = new PolicyConfig();
        c._errors.Add(reason);
        c.Sha256 = Hash("invalid\n" + reason);
        return c;
    }

    private void Load(Dictionary<string, string> kv)
    {
        var chatParts = new SortedDictionary<int, Dictionary<string, string>>();
        var denyParts = new SortedDictionary<int, Dictionary<string, string>>();

        foreach (var (key, value) in kv.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var p = key.Split(':');
            if (p.Length == 1 && RuntimeKeys.Contains(p[0])) continue; // environment-only runtime settings
            switch (p[0].ToLowerInvariant())
            {
                case "contactsallow" when p.Length == 1:
                    ContactsAllow = PhoneList(key, value);
                    break;
                case "contacts" when p.Length == 2:
                    if (p[1].Equals("mode", StringComparison.OrdinalIgnoreCase)) ContactsMode = Write(key, value) ?? WriteMode.Off;
                    else Unknown(key);
                    break;
                case "enabled" when p.Length == 1:
                    Enabled = Bool(key, value);
                    break;
                case "features" when p.Length == 2 && Enum.TryParse<GlobalSwitch>(p[1], true, out var sw)
                                                    && Enum.GetName(sw)!.Equals(p[1], StringComparison.OrdinalIgnoreCase):
                    if (Bool(key, value)) _switches.Add(sw);
                    break;
                case "defaults" when p.Length == 2:
                    switch (p[1].ToLowerInvariant())
                    {
                        case "read": DefaultRead = Bool(key, value); break;
                        case "write": DefaultWrite = Write(key, value) ?? WriteMode.Off; break;
                        case "features": DefaultFeatures = FeatureList(key, value); break;
                        case "sincedays": DefaultSinceDays = Positive(key, value); break;
                        case "notify": DefaultNotify = Bool(key, value); break;
                        default: Unknown(key); break;
                    }
                    break;
                case "events" when p.Length == 2:
                    switch (p[1].ToLowerInvariant())
                    {
                        case "enabled": EventsEnabled = Bool(key, value); break;
                        case "includeoutgoing": EventsIncludeOutgoing = Bool(key, value); break;
                        case "maxperminute": EventsMaxPerMinute = Positive(key, value) ?? EventsMaxPerMinute; break;
                        case "maxtextchars": EventsMaxTextChars = Positive(key, value) ?? EventsMaxTextChars; break;
                        case "catchupminutes": EventsCatchUpMinutes = NonNegative(key, value) ?? EventsCatchUpMinutes; break;
                        default: Unknown(key); break;
                    }
                    break;
                case "limits" when p.Length == 2:
                    switch (p[1].ToLowerInvariant())
                    {
                        case "listchats": ListChats = ListMode(key, value); break;
                        case "maxmessagespercall": MaxMessagesPerCall = Positive(key, value) ?? MaxMessagesPerCall; break;
                        case "rateperminute": RatePerMinute = Positive(key, value) ?? RatePerMinute; break;
                        case "maxmediamb": MaxMediaMb = Positive(key, value) ?? MaxMediaMb; break;
                        case "newchatsperday": NewChatsPerDay = Positive(key, value) ?? NewChatsPerDay; break;
                        case "joinsperhour": JoinsPerHour = Positive(key, value) ?? JoinsPerHour; break;
                        case "sendsperminute": SendsPerMinute = Positive(key, value) ?? SendsPerMinute; break;
                        case "mediaretentiondays": MediaRetentionDays = NonNegative(key, value) ?? MediaRetentionDays; break;
                        default: Unknown(key); break;
                    }
                    break;
                case "join" when p.Length == 2:
                    switch (p[1].ToLowerInvariant())
                    {
                        case "mode": JoinMode = Write(key, value) ?? WriteMode.Off; break;
                        case "allow": JoinAllow = AllowList(key, value); break;
                        default: Unknown(key); break;
                    }
                    break;
                case "delete" when p.Length == 2:
                    if (p[1].Equals("mode", StringComparison.OrdinalIgnoreCase))
                    {
                        var mode = Write(key, value);
                        if (mode == WriteMode.Off) _errors.Add($"{key}: must be \"draft\" or \"on\" (turn deleting off with features.deleteOwn)");
                        else DeleteMode = mode ?? WriteMode.Draft;
                    }
                    else
                    {
                        Unknown(key);
                    }
                    break;
                case "media" when p.Length == 2:
                    switch (p[1].ToLowerInvariant())
                    {
                        case "dir": MediaDir = AbsolutePath(key, value); break;
                        case "hostdir": MediaHostDir = AbsolutePath(key, value); break;
                        default: Unknown(key); break;
                    }
                    break;
                case "chats" or "deny" when p.Length == 1 && value.Trim().Length == 0:
                    break; // an empty JSON array: no rules
                case "chats" or "deny" when p.Length == 3:
                    if (!int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var idx) || idx > 9999)
                    {
                        _errors.Add($"{key}: rule index must be a number 0..9999");
                        break;
                    }
                    var target = p[0].Equals("chats", StringComparison.OrdinalIgnoreCase) ? chatParts : denyParts;
                    if (!target.TryGetValue(idx, out var d)) target[idx] = d = new(StringComparer.OrdinalIgnoreCase);
                    d[p[2]] = value;
                    break;
                default:
                    Unknown(key);
                    break;
            }
        }

        var chats = new List<ChatRule>();
        foreach (var (i, d) in chatParts)
        {
            var where = $"chats:{i}";
            foreach (var k in d.Keys.Where(k => !ChatKeys.Contains(k))) Unknown($"{where}:{k}");
            var sel = ParseSelector(where, d, ChatSelectors);
            chats.Add(new ChatRule(i, sel ?? new Selector(SelectorKind.Id, "", 0),
                d.TryGetValue("Read", out var r) ? Bool($"{where}:Read", r) : null,
                d.TryGetValue("Write", out var w) ? Write($"{where}:Write", w) : null,
                d.TryGetValue("Features", out var f) ? FeatureList($"{where}:Features", f) : null,
                d.TryGetValue("SinceDays", out var s) ? Positive($"{where}:SinceDays", s) : null,
                d.TryGetValue("Topics", out var t) ? TopicList($"{where}:Topics", t) : null,
                Label(d),
                d.TryGetValue("Notify", out var n) ? Bool($"{where}:Notify", n) : null));
            var rule = chats[^1];
            if ((rule.Notify ?? DefaultNotify) && !(rule.Read ?? DefaultRead))
                _errors.Add($"{where}: \"notify\": true needs \"read\": true (event content is message text)");
        }
        var denies = new List<DenyRule>();
        foreach (var (i, d) in denyParts)
        {
            var where = $"deny:{i}";
            foreach (var k in d.Keys.Where(k => !DenyKeys.Contains(k))) Unknown($"{where}:{k}");
            var sel = ParseSelector(where, d, DenySelectors);
            if (sel is not null) denies.Add(new DenyRule(i, sel, Label(d)));
        }
        Chats = chats;
        Denies = denies;
        if (DefaultNotify && !DefaultRead)
            _errors.Add("defaults:notify: \"notify\": true needs \"read\": true (event content is message text)");
    }

    private static readonly string[] ChatSelectors = ["Id", "Username", "Title"];
    private static readonly string[] DenySelectors = ["Id", "Username", "Type", "Folder"];
    private static readonly HashSet<string> ChatKeys = new(StringComparer.OrdinalIgnoreCase)
        { "Id", "Username", "Title", "Read", "Write", "Features", "SinceDays", "Topics", "Name", "Note", "Notify" };
    private static readonly HashSet<string> DenyKeys = new(StringComparer.OrdinalIgnoreCase)
        { "Id", "Username", "Type", "Folder", "Name", "Note" };

    private static string? Label(Dictionary<string, string> d) =>
        d.TryGetValue("Name", out var n) && n.Length > 0 ? n : d.TryGetValue("Note", out var note) && note.Length > 0 ? note : null;

    private string? AbsolutePath(string key, string v)
    {
        if (v.StartsWith('/') && !v.Split('/').Contains("..")) return v;
        _errors.Add($"{key}: expected an absolute path");
        return null;
    }

    private Selector? ParseSelector(string where, Dictionary<string, string> d, string[] allowed)
    {
        var present = allowed.Where(d.ContainsKey).ToList();
        if (present.Count != 1)
        {
            _errors.Add($"{where}: needs exactly one selector of {string.Join("|", allowed.Select(a => a.ToLowerInvariant()))}, found {present.Count}");
            return null;
        }
        var k = present[0];
        var v = d[k].Trim();
        var at = $"{where}:{k}";
        switch (k)
        {
            case "Id":
                if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) && id != 0)
                    return new Selector(SelectorKind.Id, v, id);
                _errors.Add($"{at}: expected a non-zero integer chat id");
                return null;
            case "Username":
                var u = Names.NormalizeUsername(v);
                if (u.Length is >= 1 and <= 64 && u.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
                    return new Selector(SelectorKind.Username, u);
                _errors.Add($"{at}: expected a username (letters, digits, _)");
                return null;
            case "Type":
                var kind = v.ToLowerInvariant() switch
                {
                    "private" => ChatKind.Private,
                    "group" => ChatKind.Group,
                    "channel" => ChatKind.Channel,
                    "bot" => ChatKind.Bot,
                    _ => (ChatKind?)null,
                };
                if (kind is ChatKind ck) return new Selector(SelectorKind.Type, v.ToLowerInvariant(), 0, ck);
                _errors.Add($"{at}: must be one of private|group|channel|bot");
                return null;
            default: // Title, Folder: exact text (not trimmed)
                if (d[k].Length > 0) return new Selector(k == "Title" ? SelectorKind.Title : SelectorKind.Folder, d[k]);
                _errors.Add($"{at}: empty");
                return null;
        }
    }

    private void Unknown(string key) => _errors.Add($"{key}: unknown key");

    private bool Bool(string key, string v)
    {
        if (v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        _errors.Add($"{key}: expected true|false");
        return false;
    }

    private WriteMode? Write(string key, string v)
    {
        switch (v.Trim().ToLowerInvariant())
        {
            case "off": return WriteMode.Off;
            case "draft": return WriteMode.Draft;
            case "on": return WriteMode.On;
            default:
                _errors.Add($"{key}: must be one of off|draft|on");
                return WriteMode.Off;
        }
    }

    private ListChatsMode ListMode(string key, string v)
    {
        switch (v.Trim().ToLowerInvariant())
        {
            case "allowed_only": return ListChatsMode.AllowedOnly;
            case "names_all": return ListChatsMode.NamesAll;
            case "off": return ListChatsMode.Off;
            default:
                _errors.Add($"{key}: must be one of allowed_only|names_all|off");
                return ListChatsMode.Off;
        }
    }

    private int? Positive(string key, string v)
    {
        if (int.TryParse(v.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1) return n;
        _errors.Add($"{key}: expected an integer >= 1");
        return null;
    }

    private int? NonNegative(string key, string v)
    {
        if (int.TryParse(v.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0) return n;
        _errors.Add($"{key}: expected an integer >= 0");
        return null;
    }

    private IReadOnlySet<Feature> FeatureList(string key, string v)
    {
        var set = new HashSet<Feature>();
        foreach (var item in Split(v))
        {
            if (Names.FeatureByName.TryGetValue(item, out var f)) set.Add(f);
            else _errors.Add($"{key}: unknown feature '{item}' (allowed: {string.Join(",", Names.FeatureByName.Keys)})");
        }
        return set;
    }

    private IReadOnlySet<int>? TopicList(string key, string v)
    {
        var set = new HashSet<int>();
        foreach (var item in Split(v))
        {
            if (int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t >= 1) set.Add(t);
            else _errors.Add($"{key}: topic ids must be positive integers");
        }
        return set.Count == 0 ? null : set; // empty list = no topic restriction
    }

    private List<string> PhoneList(string key, string v)
    {
        var list = new List<string>();
        var n = 0;
        foreach (var item in Split(v))
        {
            n++;
            if (PhoneNumber.TryNormalize(item, out var e164)) list.Add(e164!);
            else _errors.Add($"{key}: entry #{n} is not an E.164 phone number like +15551234567 (value not shown)");
        }
        return list;
    }

    private IReadOnlyList<JoinAllowEntry> AllowList(string key, string v)
    {
        var list = new List<JoinAllowEntry>();
        foreach (var item in Split(v))
        {
            if (JoinAllowEntry.TryParse(item, out var e)) list.Add(e!);
            else _errors.Add($"{key}: '{Redact(item)}' is not \"*\", a username, @username, a t.me link or +invitehash");
        }
        return list;
    }

    /// <summary>Invite hashes are secrets of a kind: never echo them in errors.</summary>
    private static string Redact(string item) => item.Contains('+', StringComparison.Ordinal) || item.Contains("joinchat", StringComparison.OrdinalIgnoreCase)
        ? "(invite " + JoinTarget.HashPrefix(item) + ")"
        : item;

    private static IEnumerable<string> Split(string v) =>
        v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---------------------------------------------------------------- normalized form + hash

    /// <summary>Canonical, secret-free text form of the effective config.</summary>
    public string Normalized()
    {
        static string F(IReadOnlySet<Feature>? f) => f is null ? "-" : string.Join(",", f.Select(Names.Snake).Order(StringComparer.Ordinal));
        var sb = new StringBuilder();
        sb.Append($"enabled={Enabled.ToString().ToLowerInvariant()}\n");
        foreach (var s in Enum.GetValues<GlobalSwitch>())
            sb.Append($"features.{s}={_switches.Contains(s).ToString().ToLowerInvariant()}\n");
        sb.Append($"events enabled={EventsEnabled.ToString().ToLowerInvariant()} include_outgoing={EventsIncludeOutgoing.ToString().ToLowerInvariant()} max_per_minute={EventsMaxPerMinute} max_text_chars={EventsMaxTextChars} catch_up_minutes={EventsCatchUpMinutes} default_notify={DefaultNotify.ToString().ToLowerInvariant()}\n");
        sb.Append($"defaults read={DefaultRead.ToString().ToLowerInvariant()} write={Names.Snake(DefaultWrite)} features={F(DefaultFeatures)} since_days={DefaultSinceDays?.ToString(CultureInfo.InvariantCulture) ?? "-"}\n");
        foreach (var r in Chats)
            sb.Append($"{r.Label} read={r.Read?.ToString().ToLowerInvariant() ?? "-"} write={(r.Write is WriteMode w ? Names.Snake(w) : "-")} features={F(r.Features)} since_days={r.SinceDays?.ToString(CultureInfo.InvariantCulture) ?? "-"} topics={(r.Topics is null ? "-" : string.Join(",", r.Topics.Order()))} notify={r.Notify?.ToString().ToLowerInvariant() ?? "-"}\n");
        foreach (var d in Denies) sb.Append($"{d.Label}\n");
        sb.Append($"limits list_chats={Names.Snake(ListChats)} max_messages_per_call={MaxMessagesPerCall} rate_per_minute={RatePerMinute} max_media_mb={MaxMediaMb} media_retention_days={MediaRetentionDays} sends_per_minute={SendsPerMinute} joins_per_hour={JoinsPerHour}\n");
        sb.Append($"delete mode={Names.Snake(DeleteMode)}\n");
        sb.Append($"contacts mode={Names.Snake(ContactsMode)} allow={string.Join(",", ContactsAllow.Select(PhoneNumber.Redact).Order(StringComparer.Ordinal))} new_chats_per_day={NewChatsPerDay}\n");
        sb.Append($"join mode={Names.Snake(JoinMode)} allow={string.Join(",", JoinAllow.Select(a => a.ToString()).Order(StringComparer.Ordinal))}\n");
        return sb.ToString();
    }

    private string ComputeSha(Dictionary<string, string> kv)
    {
        if (IsValid) return Hash(Normalized());
        // Invalid: hash the raw policy keys (runtime keys, which may hold secrets, are excluded).
        var raw = string.Join("\n", kv.Where(e => !RuntimeKeys.Contains(e.Key) && !EnvOnlyPolicyKeys.Contains(e.Key))
            .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .Select(e => $"{e.Key.ToLowerInvariant()}={e.Value}"));
        return Hash("invalid\n" + raw);
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
