namespace TgMcp.Domain;

/// <summary>One <c>join.allow</c> entry: <c>*</c>, a username or an invite hash.</summary>
/// <param name="Any">True for <c>*</c> (any target; dangerous).</param>
/// <param name="Target">The username or invite target (null for <c>*</c>).</param>
public sealed record JoinAllowEntry(bool Any, JoinTarget? Target)
{
    /// <summary>
    /// Parses <c>*</c>, <c>name</c>, <c>@name</c>, <c>+HASH</c> or any form <see cref="JoinTarget.TryParse"/> accepts.
    /// </summary>
    public static bool TryParse(string item, out JoinAllowEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(item);
        entry = null;
        var s = item.Trim();
        if (s == "*")
        {
            entry = new JoinAllowEntry(true, null);
            return true;
        }
        if (s.StartsWith('+') && JoinTarget.IsHash(s[1..]) && !s[1..].All(char.IsAsciiDigit))
        {
            entry = new JoinAllowEntry(false, new JoinTarget(JoinTargetKind.Invite, s[1..]));
            return true;
        }
        if (JoinTarget.IsUsername(s))
        {
            entry = new JoinAllowEntry(false, new JoinTarget(JoinTargetKind.Username, s.ToLowerInvariant()));
            return true;
        }
        if (JoinTarget.TryParse(s, out var t, out _))
        {
            entry = new JoinAllowEntry(false, t);
            return true;
        }
        return false;
    }

    /// <summary>True if this entry allows <paramref name="target"/>. Usernames ignore case; invite hashes are exact.</summary>
    public bool Matches(JoinTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Any || (Target is { } t && t.Kind == target.Kind && t.Value == target.Value);
    }

    /// <summary>Hash-free form for reports and the config hash input.</summary>
    public override string ToString() => Any ? "*" : Target!.Kind == JoinTargetKind.Invite ? Target.AuditRef : "@" + Target.Value;
}
