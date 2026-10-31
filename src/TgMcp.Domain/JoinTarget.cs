using System.Security.Cryptography;
using System.Text;

namespace TgMcp.Domain;

/// <summary>What a join target points at.</summary>
public enum JoinTargetKind
{
    /// <summary>A public group or channel by username.</summary>
    Username,
    /// <summary>A private invite link (hash).</summary>
    Invite,
}

/// <summary>
/// A parsed join target. Accepted: <c>@username</c>, <c>t.me/username</c>, <c>https://t.me/+HASH</c>,
/// <c>t.me/joinchat/HASH</c> (also <c>telegram.me</c>, <c>www.</c>, <c>http(s)://</c>, query/fragment ignored).
/// Everything else is rejected: bare names, <c>tg://</c> links, message links, phone links (<c>t.me/+123…</c>).
/// </summary>
/// <param name="Kind">Username or invite.</param>
/// <param name="Value">Lowercase username without '@', or the case-sensitive invite hash.</param>
public sealed record JoinTarget(JoinTargetKind Kind, string Value)
{
    private static readonly HashSet<string> ReservedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "joinchat", "c", "s", "addstickers", "addemoji", "addlist", "share", "proxy", "socks", "login",
        "iv", "setlanguage", "addtheme", "bg", "boost", "invoice", "contact", "m", "giftcode",
    };

    /// <summary>The invite link in canonical form, or <c>@username</c>.</summary>
    public string Canonical => Kind == JoinTargetKind.Invite ? $"https://t.me/+{Value}" : "@" + Value;

    /// <summary>Audit-safe reference: <c>@username</c>, or <c>invite#</c> plus a short sha256 prefix (never the hash).</summary>
    public string AuditRef => Kind == JoinTargetKind.Invite ? "invite#" + HashPrefix(Value) : "@" + Value;

    /// <summary>First 8 hex characters of sha256(hash).</summary>
    public static string HashPrefix(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hash)))[..8];
    }

    /// <summary>Parses <paramref name="input"/>; on failure returns false with a reason.</summary>
    public static bool TryParse(string? input, out JoinTarget? target, out string error)
    {
        target = null;
        var s = (input ?? "").Trim();
        if (s.Length == 0 || s.Length > 256 || s.Any(char.IsWhiteSpace))
        {
            error = "target is empty or contains spaces";
            return false;
        }
        if (s.StartsWith('@'))
        {
            var u = s[1..];
            if (!IsUsername(u)) { error = "not a valid @username"; return false; }
            target = new JoinTarget(JoinTargetKind.Username, u.ToLowerInvariant());
            error = "";
            return true;
        }

        var rest = s;
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (rest.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) { rest = rest[scheme.Length..]; break; }
        }
        if (rest.Contains("://", StringComparison.Ordinal))
        {
            error = "only @username, t.me/username and t.me/+invite links are accepted";
            return false;
        }
        var cut = rest.IndexOfAny(['?', '#']);
        if (cut >= 0) rest = rest[..cut];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var host = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "" : rest[(slash + 1)..].TrimEnd('/');
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        if (!host.Equals("t.me", StringComparison.OrdinalIgnoreCase) && !host.Equals("telegram.me", StringComparison.OrdinalIgnoreCase))
        {
            error = "only @username, t.me/username and t.me/+invite links are accepted";
            return false;
        }

        var parts = path.Split('/');
        if (parts.Length == 1 && parts[0].StartsWith('+'))
        {
            var hash = parts[0][1..];
            if (hash.Length > 0 && hash.All(char.IsAsciiDigit)) { error = "t.me/+<digits> is a phone-number link, not an invite"; return false; }
            if (!IsHash(hash)) { error = "not a valid invite link"; return false; }
            target = new JoinTarget(JoinTargetKind.Invite, hash);
            error = "";
            return true;
        }
        if (parts.Length == 2 && parts[0].Equals("joinchat", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsHash(parts[1])) { error = "not a valid invite link"; return false; }
            target = new JoinTarget(JoinTargetKind.Invite, parts[1]);
            error = "";
            return true;
        }
        if (parts.Length == 1 && IsUsername(parts[0]) && !ReservedPaths.Contains(parts[0]))
        {
            target = new JoinTarget(JoinTargetKind.Username, parts[0].ToLowerInvariant());
            error = "";
            return true;
        }
        error = "not a group/channel link (message, sticker, proxy and other t.me links are not accepted)";
        return false;
    }

    /// <summary>Telegram usernames: 4-32 characters, a letter first, then letters, digits or '_'.</summary>
    internal static bool IsUsername(string u) =>
        u.Length is >= 4 and <= 32 && char.IsAsciiLetter(u[0]) && u.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !u.EndsWith('_');

    /// <summary>Invite hashes are URL-safe base64-like strings.</summary>
    internal static bool IsHash(string h) =>
        h.Length is >= 8 and <= 128 && h.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
