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
                r.Features ?? c.DefaultFeatures, r.SinceDays ?? c.DefaultSinceDays, r.Topics, r.Notify ?? c.DefaultNotify);
        }
        yield return "  defaults -> " + Effective(c, c.DefaultRead, c.DefaultWrite, c.DefaultFeatures, c.DefaultSinceDays, null, c.DefaultNotify);
        var notifying = c.Chats.Where(r => r.Notify ?? c.DefaultNotify).Select(r => $"chats[{r.Index}]").ToList();
        if (c.DefaultNotify) notifying.Add("defaults (not for bot/channel chats or bot senders)");
        yield return c.EventsEnabled
            ? $"events: ON -> notify from {(notifying.Count == 0 ? "no rule (nothing will be emitted)" : string.Join(", ", notifying))}"
            : $"events: off{(notifying.Count > 0 ? " (rules with notify have no effect until events.enabled is true)" : "")}";
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
        yield return ("delete:mode", "delete.mode", Names.Snake(c.DeleteMode));
        yield return ("react:mode", "react.mode", Names.Snake(c.ReactMode) + " (stricter of this and the chat's write mode)");
        yield return ("contacts:mode", "contacts.mode", Names.Snake(c.ContactsMode));
        yield return ("ContactsAllow", "TGMCP_ContactsAllow", c.ContactsAllow.Count == 0 ? "(none)"
            : $"{c.ContactsAllow.Count} number(s): " + string.Join(", ", c.ContactsAllow.Select(PhoneNumber.Redact)));
        yield return ("limits:newChatsPerDay", "limits.newChatsPerDay", c.NewChatsPerDay.ToString(CultureInfo.InvariantCulture));
        yield return ("join:mode", "join.mode", Names.Snake(c.JoinMode));
        yield return ("join:allow", "join.allow", c.JoinAllow.Count == 0 ? "[]" : "[" + string.Join(", ", c.JoinAllow) + "]"
            + (c.JoinAllow.Any(a => a.Any) ? " (WARNING: \"*\" allows joining anything)" : ""));
        yield return ("limits:joinsPerHour", "limits.joinsPerHour", c.JoinsPerHour.ToString(CultureInfo.InvariantCulture));
        yield return ("defaults:notify", "defaults.notify", B(c.DefaultNotify));
        yield return ("events:enabled", "events.enabled", B(c.EventsEnabled));
        yield return ("events:includeOutgoing", "events.includeOutgoing", B(c.EventsIncludeOutgoing));
        yield return ("events:maxPerMinute", "events.maxPerMinute", c.EventsMaxPerMinute.ToString(CultureInfo.InvariantCulture));
        yield return ("events:maxTextChars", "events.maxTextChars", c.EventsMaxTextChars.ToString(CultureInfo.InvariantCulture));
        yield return ("events:catchUpMinutes", "events.catchUpMinutes", c.EventsCatchUpMinutes == 0 ? "0 (off)" : c.EventsCatchUpMinutes.ToString(CultureInfo.InvariantCulture));
        yield return ("media:dir", "media.dir", c.MediaDir ?? "{dataDir}/media");
        yield return ("media:hostDir", "media.hostDir", c.MediaHostDir ?? "none");
    }

    private static string Features(IReadOnlySet<Feature> f) =>
        "[" + string.Join(", ", f.Select(Names.Snake).Order(StringComparer.Ordinal)) + "]";

    private static string Effective(PolicyConfig c, bool read, WriteMode write, IReadOnlySet<Feature> feats, int? since, IReadOnlySet<int>? topics, bool notify)
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
        if (notify) parts.Add("notify=true" + (c.EventsEnabled ? "" : " (inactive: events.enabled is false)"));
        return string.Join(" ", parts);
    }
}
