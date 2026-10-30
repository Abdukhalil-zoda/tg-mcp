using Microsoft.Extensions.Options;
using TgMcp.Application.Telegram;

namespace TgMcp.Infrastructure.Telegram;

/// <summary>
/// Copies downloaded files to <c>{DataDir}/media/{chat}_{message}_{name}</c>.
/// TODO: delete files older than 7 days (planned for a later version).
/// </summary>
public sealed class FileMediaStore : IMediaStore
{
    private readonly TelegramOptions _o;

    /// <summary>Creates the store.</summary>
    public FileMediaStore(IOptions<TelegramOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _o = options.Value;
    }

    /// <inheritdoc />
    public async Task<string> StoreAsync(string sourcePath, long chatId, long messageId, string? fileName, CancellationToken ct)
    {
        Directory.CreateDirectory(_o.MediaDir);
        var safe = Sanitize(fileName ?? Path.GetFileName(sourcePath));
        var target = Path.Combine(_o.MediaDir, $"{chatId}_{messageId}_{safe}");
        var src = File.OpenRead(sourcePath);
        await using (src.ConfigureAwait(false))
        {
            var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (dst.ConfigureAwait(false))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
        }
        return target;
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        var s = new string(chars).TrimStart('.');
        return s.Length == 0 ? "file" : s.Length > 100 ? s[^100..] : s;
    }
}
