using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgMcp.Application.Telegram;

namespace TgMcp.Infrastructure.Telegram;

/// <summary>
/// Copies downloaded files to <c>{MediaDir}/{chat}_{message}_{name}</c> (default MediaDir: {DataDir}/media).
/// Files older than the retention period are deleted by <see cref="Cleanup"/>.
/// </summary>
public sealed partial class FileMediaStore : IMediaStore
{
    private readonly TelegramOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger<FileMediaStore> _log;

    /// <summary>Creates the store.</summary>
    public FileMediaStore(IOptions<TelegramOptions> options, TimeProvider time, ILogger<FileMediaStore> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        _o = options.Value;
        _time = time;
        _log = log;
    }

    /// <inheritdoc />
    public int Cleanup(int retentionDays)
    {
        var dir = _o.EffectiveMediaDir;
        if (retentionDays <= 0 || !Directory.Exists(dir)) return 0;
        var cutoff = _time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(retentionDays);
        var deleted = 0;
        try
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles())
            {
                if (file.LastWriteTimeUtc >= cutoff) continue;
                try
                {
                    file.Delete();
                    deleted++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    LogDeleteFailed(file.Name, e.Message);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(dir, e.Message);
        }
        if (deleted > 0) LogDeleted(deleted, retentionDays);
        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "media cleanup deleted {Count} file(s) older than {Days} day(s)")]
    private partial void LogDeleted(int count, int days);

    [LoggerMessage(Level = LogLevel.Warning, Message = "media cleanup could not delete {Name}: {Error}")]
    private partial void LogDeleteFailed(string name, string error);

    /// <inheritdoc />
    public async Task<StoredMedia> StoreAsync(string sourcePath, long chatId, long messageId, string? fileName, CancellationToken ct)
    {
        var mediaDir = _o.EffectiveMediaDir;
        Directory.CreateDirectory(mediaDir);
        var safe = Sanitize(fileName ?? Path.GetFileName(sourcePath));
        var target = Path.Combine(mediaDir, $"{chatId}_{messageId}_{safe}");
        var src = File.OpenRead(sourcePath);
        await using (src.ConfigureAwait(false))
        {
            var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (dst.ConfigureAwait(false))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
        }
        return new StoredMedia(target, MediaPaths.ToHostPath(target, mediaDir, _o.MediaHostDir));
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        var s = new string(chars).TrimStart('.');
        return s.Length == 0 ? "file" : s.Length > 100 ? s[^100..] : s;
    }
}
