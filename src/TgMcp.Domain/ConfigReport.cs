namespace TgMcp.Domain;

/// <summary>Human-readable effective result per rule, for `tgmcp config check`. Never prints secrets.</summary>
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
        yield return c.Enabled ? "enabled: true" : "enabled: false -> everything is denied (set TGMCP_Enabled=true)";
        var on = Enum.GetValues<GlobalSwitch>().Where(c.Switches.Contains).Select(s => s.ToString()).ToList();
        yield return $"global switches on: {(on.Count == 0 ? "(none)" : string.Join(", ", on))}";
        yield return $"limits: list_chats={Names.Snake(c.ListChats)} max_messages_per_call={c.MaxMessagesPerCall} rate_per_minute={c.RatePerMinute} max_media_mb={c.MaxMediaMb}";
        yield return "deny rules (always win):" + (c.Denies.Count == 0 ? " (none)" : "");
        foreach (var d in c.Denies) yield return $"  {d.Label}";
        yield return "chat rules (first match wins; unset keys fall back to defaults):" + (c.Chats.Count == 0 ? " (none)" : "");
        foreach (var r in c.Chats)
            yield return "  " + r.Label + " -> " + Effective(c, r.Read ?? c.DefaultRead, r.Write ?? c.DefaultWrite,
                r.Features ?? c.DefaultFeatures, r.SinceDays ?? c.DefaultSinceDays, r.Topics);
        yield return "  defaults -> " + Effective(c, c.DefaultRead, c.DefaultWrite, c.DefaultFeatures, c.DefaultSinceDays, null);
    }

    private static string Effective(PolicyConfig c, bool read, WriteMode write, IReadOnlySet<Feature> feats, int? since, IReadOnlySet<int>? topics)
    {
        string Gate(bool wanted, GlobalSwitch sw) => !wanted ? "" : c.Switches.Contains(sw) ? "" : $" (inactive: global {sw} off)";
        var parts = new List<string>
        {
            $"read={read.ToString().ToLowerInvariant()}{Gate(read, GlobalSwitch.Read)}",
            $"write={Names.Snake(write)}{Gate(write != WriteMode.Off, GlobalSwitch.Send)} (write tools are not in this version)",
        };
        var f = feats.Select(x => Names.Snake(x) + Gate(true, Enum.Parse<GlobalSwitch>(x.ToString()))).Order(StringComparer.Ordinal).ToList();
        parts.Add($"features=[{string.Join(", ", f)}]");
        if (since is int s) parts.Add($"since_days={s}");
        if (topics is not null) parts.Add($"topics=[{string.Join(",", topics.Order())}]");
        return string.Join(" ", parts);
    }
}
