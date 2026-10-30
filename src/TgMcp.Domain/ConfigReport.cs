using System.Globalization;

namespace TgMcp.Domain;

/// <summary>Human-readable effective result per setting and rule, for <c>tgmcp config check</c>. Never prints secrets.</summary>
public static class ConfigReport
{
    /// <summary>Report lines for <paramref name="c"/>.</summary>
    public static IEnumerable<string> Describe(PolicyConfig c)
    {
        ArgumentNullException.ThrowIfNull(c);
        yield return $"config sha256: {c.Sha256}";
        if (!c.IsValid)
        {
            yield return $"INVALID: {c.Errors.Count} error(s) -> the server will start but DENY EVERYTHING:";
            foreach (var e in c.Errors) yield return $"  error: {e}";
            yield break;
        }
        if (!c.Enabled) yield return "NOTE: \"enabled\" is false -> everything is denied";

        yield return "settings (value [source]; precedence: default < file < env):";
        foreach (var (key, name, value) in Settings(c)) yield return $"  {name} = {value} [{c.SourceOf(key)}]";

        yield return "deny rules (always win):" + (c.Denies.Count == 0 ? " (none)" : "");
        foreach (var d in c.Denies) yield return $"  {d.Label}{NameOf(d.Name)} [{c.SourceOf($"deny:{d.Index}")}]";

        yield return "chat rules (first match wins; unset keys fall back to defaults):" + (c.Chats.Count == 0 ? " (none)" : "");
        foreach (var r in c.Chats)
        {
            yield return $"  {r.Label}{NameOf(r.Name)} [{c.SourceOf($"chats:{r.Index}")}] -> " + Effective(c, r.Read ?? c.DefaultRead, r.Write ?? c.DefaultWrite,
                r.Features ?? c.DefaultFeatures, r.SinceDays ?? c.DefaultSinceDays, r.Topics);
        }
        yield return "  defaults -> " + Effective(c, c.DefaultRead, c.DefaultWrite, c.DefaultFeatures, c.DefaultSinceDays, null);
    }

    private static string NameOf(string? name) => name is null ? "" : $" \"{name}\"";

    private static IEnumerable<(string Key, string Name, string Value)> Settings(PolicyConfig c)
    {
        static string B(bool b) => b ? "true" : "false";
        yield return ("enabled", "enabled", B(c.Enabled));
        foreach (var s in Enum.GetValues<GlobalSwitch>())
            yield return ($"features:{s}", $"features.{Names.Camel(s)}", B(c.Switches.Contains(s)));
        yield return ("defaults:read", "defaults.read", B(c.DefaultRead));
        yield return ("defaults:write", "defaults.write", Names.Snake(c.DefaultWrite));
        yield return ("defaults:features", "defaults.features", Features(c.DefaultFeatures));
        yield return ("defaults:sinceDays", "defaults.sinceDays", c.DefaultSinceDays?.ToString(CultureInfo.InvariantCulture) ?? "none");
        yield return ("limits:listChats", "limits.listChats", Names.Snake(c.ListChats));
        yield return ("limits:maxMessagesPerCall", "limits.maxMessagesPerCall", c.MaxMessagesPerCall.ToString(CultureInfo.InvariantCulture));
        yield return ("limits:ratePerMinute", "limits.ratePerMinute", c.RatePerMinute.ToString(CultureInfo.InvariantCulture));
        yield return ("limits:sendsPerMinute", "limits.sendsPerMinute", c.SendsPerMinute.ToString(CultureInfo.InvariantCulture));
        yield return ("limits:maxMediaMb", "limits.maxMediaMb", c.MaxMediaMb.ToString(CultureInfo.InvariantCulture));
        yield return ("limits:mediaRetentionDays", "limits.mediaRetentionDays", c.MediaRetentionDays == 0 ? "0 (keep forever)" : c.MediaRetentionDays.ToString(CultureInfo.InvariantCulture));
        yield return ("media:dir", "media.dir", c.MediaDir ?? "{dataDir}/media");
        yield return ("media:hostDir", "media.hostDir", c.MediaHostDir ?? "none");
    }

    private static string Features(IReadOnlySet<Feature> f) =>
        "[" + string.Join(", ", f.Select(Names.Snake).Order(StringComparer.Ordinal)) + "]";

    private static string Effective(PolicyConfig c, bool read, WriteMode write, IReadOnlySet<Feature> feats, int? since, IReadOnlySet<int>? topics)
    {
        string Gate(bool wanted, GlobalSwitch sw) => !wanted || c.Switches.Contains(sw) ? "" : $" (inactive: features.{Names.Camel(sw)} is off)";
        var parts = new List<string>
        {
            $"read={(read ? "true" : "false")}{Gate(read, GlobalSwitch.Read)}",
            $"write={Names.Snake(write)}{Gate(write != WriteMode.Off, GlobalSwitch.Send)}",
        };
        var f = feats.Select(x => Names.Snake(x) + Gate(true, Enum.Parse<GlobalSwitch>(x.ToString()))).Order(StringComparer.Ordinal).ToList();
        parts.Add($"features=[{string.Join(", ", f)}]");
        if (since is int s) parts.Add($"sinceDays={s}");
        if (topics is not null) parts.Add($"topics=[{string.Join(",", topics.Order())}]");
        return string.Join(" ", parts);
    }
}
