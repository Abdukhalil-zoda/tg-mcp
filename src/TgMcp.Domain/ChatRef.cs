using System.Globalization;

namespace TgMcp.Domain;

/// <summary>One <c>addMember.chats</c> entry: a chat id or a public @username. There is no wildcard.</summary>
/// <param name="Id">Chat id (0 for a username entry).</param>
/// <param name="Username">Lowercase username without '@' (null for an id entry).</param>
public sealed record ChatRef(long Id, string? Username)
{
    /// <summary>Parses a non-zero chat id, <c>@name</c> or <c>name</c>.</summary>
    public static bool TryParse(string item, out ChatRef? entry)
    {
        ArgumentNullException.ThrowIfNull(item);
        entry = null;
        var s = item.Trim();
        if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
        {
            if (id == 0) return false;
            entry = new ChatRef(id, null);
            return true;
        }
        var u = s.StartsWith('@') ? s[1..] : s;
        if (!JoinTarget.IsUsername(u)) return false;
        entry = new ChatRef(0, u.ToLowerInvariant());
        return true;
    }

    /// <summary>True if this entry names <paramref name="chat"/> (by id, or by any of its usernames ignoring case).</summary>
    public bool Matches(ChatInfo chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        return Username is null ? chat.Id == Id : chat.Usernames.Any(u => Names.NormalizeUsername(u) == Username);
    }

    /// <summary>Report form: the id or <c>@username</c>.</summary>
    public override string ToString() => Username is null ? Id.ToString(CultureInfo.InvariantCulture) : "@" + Username;
}
