using System.Text;

namespace TgMcp.Infrastructure.Telegram;

/// <summary>
/// TDLib settings, bound from the root of the TGMCP_ configuration
/// (TGMCP_ApiId, TGMCP_ApiHash, TGMCP_DatabaseKey or TGMCP_DatabaseKeyFile, TGMCP_DataDir).
/// Values are never logged.
/// </summary>
public sealed class TelegramOptions
{
    /// <summary>api_id from my.telegram.org (kept as text so a malformed value cannot break binding).</summary>
    public string? ApiId { get; set; }

    /// <summary>The parsed api_id, or 0 if missing or malformed.</summary>
    public int ParsedApiId =>
        int.TryParse(ApiId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0;

    /// <summary>api_hash from my.telegram.org.</summary>
    public string ApiHash { get; set; } = "";

    /// <summary>TDLib database encryption key (inline).</summary>
    public string? DatabaseKey { get; set; }

    /// <summary>Path of a file holding the database encryption key (used when <see cref="DatabaseKey"/> is empty).</summary>
    public string? DatabaseKeyFile { get; set; }

    /// <summary>Data root (default /data): tdlib/, files/, media/ live below it.</summary>
    public string DataDir { get; set; } = "/data";

    /// <summary>Per-request timeout.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for a synchronous file download.</summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>TDLib database directory.</summary>
    public string DatabaseDir => Path.Combine(DataDir, "tdlib");

    /// <summary>TDLib files directory.</summary>
    public string FilesDir => Path.Combine(DataDir, "files");

    /// <summary>Directory for media handed to the client.</summary>
    public string MediaDir => Path.Combine(DataDir, "media");

    /// <summary>The encryption key bytes, or empty if not configured.</summary>
    public byte[] ResolveDatabaseKey()
    {
        if (!string.IsNullOrEmpty(DatabaseKey)) return Encoding.UTF8.GetBytes(DatabaseKey);
        if (!string.IsNullOrEmpty(DatabaseKeyFile) && File.Exists(DatabaseKeyFile))
            return Encoding.UTF8.GetBytes(File.ReadAllText(DatabaseKeyFile).Trim());
        return [];
    }

    /// <summary>Null if TDLib can be started, else why not (names only, never values).</summary>
    public string? Problem()
    {
        if (ParsedApiId <= 0 || string.IsNullOrWhiteSpace(ApiHash))
            return "TGMCP_ApiId/TGMCP_ApiHash are not set (the owner must provide api.env)";
        if (ResolveDatabaseKey().Length == 0)
            return "TGMCP_DatabaseKey or TGMCP_DatabaseKeyFile is not set";
        return null;
    }

    /// <summary>True if a TDLib database exists (i.e. login has been run).</summary>
    public bool HasSession() =>
        Directory.Exists(DatabaseDir) && Directory.EnumerateFileSystemEntries(DatabaseDir).Any();
}
