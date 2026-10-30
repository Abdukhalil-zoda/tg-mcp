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

/// <summary>A <c>Chats__N</c> rule; null permissions fall back to Defaults.</summary>
/// <param name="Index">N.</param>
/// <param name="Selector">The rule's selector.</param>
/// <param name="Read">Read permission.</param>
/// <param name="Write">Write mode.</param>
/// <param name="Features">Per-chat features.</param>
/// <param name="SinceDays">Maximum message age in days.</param>
/// <param name="Topics">Allowed forum topic ids.</param>
public sealed record ChatRule(
    int Index,
    Selector Selector,
    bool? Read,
    WriteMode? Write,
    IReadOnlySet<Feature>? Features,
    int? SinceDays,
    IReadOnlySet<int>? Topics)
{
    /// <summary>Label used in decisions and audit.</summary>
    public string Label => $"chats[{Index}] {Selector}";
}

/// <summary>A <c>Deny__N</c> rule. Deny always wins.</summary>
/// <param name="Index">N.</param>
/// <param name="Selector">The rule's selector.</param>
public sealed record DenyRule(int Index, Selector Selector)
{
    /// <summary>Label used in decisions and audit.</summary>
    public string Label => $"deny[{Index}] {Selector}";
}

/// <summary>
/// The effective configuration parsed from TGMCP_ environment keys (IConfiguration form, ':' separated,
/// prefix already stripped). Any error makes the whole config invalid => everything is denied.
/// </summary>
public sealed class PolicyConfig
{
    /// <summary>Environment variable prefix.</summary>
    public const string Prefix = "TGMCP_";

    /// <summary>Non-policy keys the process itself reads. Their values are never logged or hashed.</summary>
    public static readonly IReadOnlySet<string> RuntimeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ApiId", "ApiHash", "DatabaseKey", "DatabaseKeyFile", "AuditPath", "DataDir",
    };

    /// <summary>Master switch (default false = deny all).</summary>
    public bool Enabled { get; private set; }
    /// <summary>Global switches that are on.</summary>
    public IReadOnlySet<GlobalSwitch> Switches => _switches;
    /// <summary>Defaults__Read.</summary>
    public bool DefaultRead { get; private set; }
    /// <summary>Defaults__Write.</summary>
    public WriteMode DefaultWrite { get; private set; } = WriteMode.Off;
    /// <summary>Defaults__Features.</summary>
    public IReadOnlySet<Feature> DefaultFeatures { get; private set; } = Names.NoFeatures;
    /// <summary>Defaults__SinceDays.</summary>
    public int? DefaultSinceDays { get; private set; }
    /// <summary>Chats rules ordered by N.</summary>
    public IReadOnlyList<ChatRule> Chats { get; private set; } = [];
    /// <summary>Deny rules ordered by N.</summary>
    public IReadOnlyList<DenyRule> Denies { get; private set; } = [];
    /// <summary>Limits__ListChats.</summary>
    public ListChatsMode ListChats { get; private set; } = ListChatsMode.AllowedOnly;
    /// <summary>Limits__MaxMessagesPerCall.</summary>
    public int MaxMessagesPerCall { get; private set; } = 200;
    /// <summary>Limits__RatePerMinute.</summary>
    public int RatePerMinute { get; private set; } = 30;
    /// <summary>Limits__MaxMediaMb.</summary>
    public int MaxMediaMb { get; private set; } = 50;

    /// <summary>Validation errors (key names, never secret values).</summary>
    public IReadOnlyList<string> Errors => _errors;
    /// <summary>True if there are no errors.</summary>
    public bool IsValid => _errors.Count == 0;
    /// <summary>sha256 of the normalized effective config (or of the raw policy keys if invalid).</summary>
    public string Sha256 { get; private set; } = "";

    private readonly HashSet<GlobalSwitch> _switches = [];
    private readonly List<string> _errors = [];

    private PolicyConfig() { }

    /// <summary>Raw environment (e.g. Environment.GetEnvironmentVariables()) -> config. Keys without the prefix are ignored.</summary>
    public static PolicyConfig FromEnvironment(IDictionary env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var list = new List<KeyValuePair<string, string?>>();
        foreach (DictionaryEntry e in env)
        {
            var k = e.Key?.ToString() ?? "";
            if (!k.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new(k[Prefix.Length..].Replace("__", ":"), e.Value?.ToString()));
        }
        return Parse(list);
    }

    /// <summary>Test/CLI helper: (key, value) pairs in env form (<c>TGMCP_</c> prefix optional, <c>__</c> nesting).</summary>
    public static PolicyConfig FromPairs(params (string Key, string Value)[] pairs) =>
        Parse(pairs is null ? [] : pairs.Select(p => new KeyValuePair<string, string?>(
            (p.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? p.Key[Prefix.Length..] : p.Key).Replace("__", ":"),
            p.Value)));

    /// <summary>Entries as produced by IConfiguration.AsEnumerable() (section keys with null values are skipped).</summary>
    public static PolicyConfig Parse(IEnumerable<KeyValuePair<string, string?>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var c = new PolicyConfig();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            if (value is null) continue;
            if (!seen.TryAdd(key, value)) c._errors.Add($"{key}: duplicate key (differs only in case?)");
        }
        c.Load(seen);
        for (var i = 0; i < c._errors.Count; i++) c._errors[i] = EnvForm(c._errors[i]);
        c.Sha256 = c.ComputeSha(seen);
        return c;
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
            if (p.Length == 1 && RuntimeKeys.Contains(p[0])) continue;
            switch (p[0].ToLowerInvariant())
            {
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
                        default: Unknown(key); break;
                    }
                    break;
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
            var where = $"Chats:{i}";
            foreach (var k in d.Keys.Where(k => !ChatKeys.Contains(k))) Unknown($"{where}:{k}");
            var sel = ParseSelector(where, d, ChatSelectors);
            chats.Add(new ChatRule(i, sel ?? new Selector(SelectorKind.Id, "", 0),
                d.TryGetValue("Read", out var r) ? Bool($"{where}:Read", r) : null,
                d.TryGetValue("Write", out var w) ? Write($"{where}:Write", w) : null,
                d.TryGetValue("Features", out var f) ? FeatureList($"{where}:Features", f) : null,
                d.TryGetValue("SinceDays", out var s) ? Positive($"{where}:SinceDays", s) : null,
                d.TryGetValue("Topics", out var t) ? TopicList($"{where}:Topics", t) : null));
        }
        var denies = new List<DenyRule>();
        foreach (var (i, d) in denyParts)
        {
            var where = $"Deny:{i}";
            foreach (var k in d.Keys.Where(k => !DenySelectors.Contains(k))) Unknown($"{where}:{k}");
            var sel = ParseSelector(where, d, DenySelectors);
            if (sel is not null) denies.Add(new DenyRule(i, sel));
        }
        Chats = chats;
        Denies = denies;
    }

    private static readonly string[] ChatSelectors = ["Id", "Username", "Title"];
    private static readonly string[] DenySelectors = ["Id", "Username", "Type", "Folder"];
    private static readonly HashSet<string> ChatKeys = new(StringComparer.OrdinalIgnoreCase)
        { "Id", "Username", "Title", "Read", "Write", "Features", "SinceDays", "Topics" };

    private Selector? ParseSelector(string where, Dictionary<string, string> d, string[] allowed)
    {
        var present = allowed.Where(d.ContainsKey).ToList();
        if (present.Count != 1)
        {
            _errors.Add($"{where}: needs exactly one selector of {string.Join("|", allowed)}, found {present.Count}");
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

    /// <summary>"Chats:0:Id: msg" -> "TGMCP_Chats__0__Id: msg" so errors name the variable the owner set.</summary>
    private static string EnvForm(string error)
    {
        var i = error.IndexOf(": ", StringComparison.Ordinal);
        return i <= 0 ? error : Prefix + error[..i].Replace(":", "__", StringComparison.Ordinal) + error[i..];
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

    private IReadOnlySet<int> TopicList(string key, string v)
    {
        var set = new HashSet<int>();
        foreach (var item in Split(v))
        {
            if (int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t >= 1) set.Add(t);
            else _errors.Add($"{key}: topic ids must be positive integers");
        }
        if (set.Count == 0) _errors.Add($"{key}: empty topic list (remove the key to allow all topics)");
        return set;
    }

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
        sb.Append($"defaults read={DefaultRead.ToString().ToLowerInvariant()} write={Names.Snake(DefaultWrite)} features={F(DefaultFeatures)} since_days={DefaultSinceDays?.ToString(CultureInfo.InvariantCulture) ?? "-"}\n");
        foreach (var r in Chats)
            sb.Append($"{r.Label} read={r.Read?.ToString().ToLowerInvariant() ?? "-"} write={(r.Write is WriteMode w ? Names.Snake(w) : "-")} features={F(r.Features)} since_days={r.SinceDays?.ToString(CultureInfo.InvariantCulture) ?? "-"} topics={(r.Topics is null ? "-" : string.Join(",", r.Topics.Order()))}\n");
        foreach (var d in Denies) sb.Append($"{d.Label}\n");
        sb.Append($"limits list_chats={Names.Snake(ListChats)} max_messages_per_call={MaxMessagesPerCall} rate_per_minute={RatePerMinute} max_media_mb={MaxMediaMb}\n");
        return sb.ToString();
    }

    private string ComputeSha(Dictionary<string, string> kv)
    {
        if (IsValid) return Hash(Normalized());
        // Invalid: hash the raw policy keys (runtime keys, which may hold secrets, are excluded).
        var raw = string.Join("\n", kv.Where(e => !RuntimeKeys.Contains(e.Key))
            .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .Select(e => $"{e.Key.ToLowerInvariant()}={e.Value}"));
        return Hash("invalid\n" + raw);
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
