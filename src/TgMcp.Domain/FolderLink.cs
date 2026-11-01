namespace TgMcp.Domain;

/// <summary>
/// A chat folder invite link: <c>https://t.me/addlist/SLUG</c> (also <c>t.me/…</c>, <c>telegram.me</c>, <c>www.</c>, <c>http://</c>;
/// query and fragment ignored). The slug works like a password for the folder: it is never logged or echoed in errors.
/// </summary>
/// <param name="Slug">The case-sensitive link slug.</param>
public sealed record FolderLink(string Slug)
{
    /// <summary>The link in canonical form.</summary>
    public string Canonical => "https://t.me/addlist/" + Slug;

    /// <summary>Audit-safe reference: <c>addlist#</c> plus a short sha256 prefix of the slug.</summary>
    public string AuditRef => "addlist#" + JoinTarget.HashPrefix(Slug);

    /// <summary>Parses <paramref name="input"/>; on failure returns false with a reason that never contains the input.</summary>
    public static bool TryParse(string? input, out FolderLink? link, out string error)
    {
        link = null;
        var s = (input ?? "").Trim();
        error = "expected a chat folder link like https://t.me/addlist/<slug>";
        if (s.Length == 0 || s.Length > 256 || s.Any(char.IsWhiteSpace)) return false;
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (s.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) { s = s[scheme.Length..]; break; }
        }
        var cut = s.IndexOfAny(['?', '#']);
        if (cut >= 0) s = s[..cut];
        var parts = s.TrimEnd('/').Split('/');
        if (parts.Length != 3) return false;
        var host = parts[0].StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? parts[0][4..] : parts[0];
        if (!host.Equals("t.me", StringComparison.OrdinalIgnoreCase) && !host.Equals("telegram.me", StringComparison.OrdinalIgnoreCase)) return false;
        if (!parts[1].Equals("addlist", StringComparison.OrdinalIgnoreCase) || !JoinTarget.IsHash(parts[2])) return false;
        link = new FolderLink(parts[2]);
        error = "";
        return true;
    }
}
