using System.Text.RegularExpressions;

namespace TgMcp.Domain;

/// <summary>E.164 phone numbers: normalization, validation and redaction (numbers are never logged in clear).</summary>
public static partial class PhoneNumber
{
    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164();

    /// <summary>Removes spaces, dashes, dots and parentheses and checks the E.164 form (<c>+</c> and 8-15 digits).</summary>
    public static bool TryNormalize(string? input, out string? e164)
    {
        var s = new string((input ?? "").Where(c => !char.IsWhiteSpace(c) && c is not '-' and not '.' and not '(' and not ')').ToArray());
        e164 = E164().IsMatch(s) ? s : null;
        return e164 is not null;
    }

    /// <summary><c>+…67 (#1a2b3c4d)</c>: the last two digits and a sha256 prefix, never the number.</summary>
    public static string Redact(string e164)
    {
        ArgumentNullException.ThrowIfNull(e164);
        return $"+…{e164[^2..]} (#{JoinTarget.HashPrefix(e164)})";
    }

    /// <summary>Audit reference: <c>phone #1a2b3c4d</c>.</summary>
    public static string AuditRef(string e164) => "phone #" + JoinTarget.HashPrefix(e164);
}
