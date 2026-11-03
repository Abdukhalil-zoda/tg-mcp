using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using TgMcp.Application.Tools;

namespace TgMcp.Infrastructure.Telegram;

/// <summary>
/// Export files in <c>{DataDir}/exports</c>: directory 0700, files 0600, written to a temp file and renamed.
/// Each write deletes this store's files older than <see cref="Retention"/> (by the UTC time in the file name).
/// </summary>
public sealed class FileExportStore : IExportStore, IDisposable
{
    /// <summary>How long export files are kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(2);

    private const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _dir;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store.</summary>
    public FileExportStore(IOptions<TelegramOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dir = Path.Combine(options.Value.DataDir, "exports");
        _time = time;
    }

    /// <summary>The export directory.</summary>
    public string ExportDir => _dir;

    /// <inheritdoc />
    public async Task<StoredExport> WriteAsync(string prefix, string json, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(json);
        if (prefix.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))) throw new ArgumentException("bad export prefix", nameof(prefix));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureDirectory();
            var now = _time.GetUtcNow();
            Prune(prefix, now);
            var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var name = $"{prefix}-{stamp}.json";
            for (var i = 1; File.Exists(Path.Combine(_dir, name)); i++) name = $"{prefix}-{stamp}-{i}.json";
            var path = Path.Combine(_dir, name);
            var tmp = Path.Combine(_dir, "." + name + ".tmp");
            var fs = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) fs.UnixCreateMode = FileMode600;
            var stream = new FileStream(tmp, fs);
            await using (stream.ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(tmp, path, overwrite: false);
            return new StoredExport(path, name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_dir);
            return;
        }
        Directory.CreateDirectory(_dir, DirMode);
        File.SetUnixFileMode(_dir, DirMode);
    }

    private void Prune(string prefix, DateTimeOffset now)
    {
        var cutoff = now.UtcDateTime - Retention;
        foreach (var file in Directory.EnumerateFiles(_dir, prefix + "-*.json"))
        {
            try
            {
                if (WrittenAt(prefix, file) < cutoff) File.Delete(file);
            }
            catch (IOException)
            {
                // best effort: the next export tries again
            }
        }
    }

    /// <summary>The UTC time in the file name (the store's clock), else the file's last write time.</summary>
    private static DateTime WrittenAt(string prefix, string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var stamp = name.Length >= prefix.Length + 17 ? name.Substring(prefix.Length + 1, 16) : "";
        return DateTime.TryParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : File.GetLastWriteTimeUtc(file);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
