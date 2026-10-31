using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TgMcp.Application.Telegram;

namespace TgMcp.Infrastructure.Telegram;

/// <summary>
/// The sent-log in <c>{DataDir}/sent-log.json</c>: chat and message ids of messages this server sent
/// (owner-only, atomic replace, at most <see cref="Capacity"/> entries, oldest dropped). No text is stored.
/// </summary>
public sealed class FileSentLog : ISentLog, IDisposable
{
    /// <summary>Most entries kept.</summary>
    public const int Capacity = 10_000;

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<string>? _entries;

    /// <summary>Creates the log.</summary>
    public FileSentLog(IOptions<TelegramOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = Path.Combine(options.Value.DataDir, "sent-log.json");
    }

    private static string Key(long chat, long message) =>
        chat.ToString(CultureInfo.InvariantCulture) + ":" + message.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public async Task RecordAsync(long chatId, long messageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = await LoadAsync(ct).ConfigureAwait(false);
            var key = Key(chatId, messageId);
            list.Remove(key);
            list.Add(key);
            if (list.Count > Capacity) list.RemoveRange(0, list.Count - Capacity);
            await SaveAsync(list, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ContainsAsync(long chatId, long messageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await LoadAsync(ct).ConfigureAwait(false)).Contains(Key(chatId, messageId));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(long chatId, IReadOnlyCollection<long> messageIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = await LoadAsync(ct).ConfigureAwait(false);
            var keys = messageIds.Select(m => Key(chatId, m)).ToHashSet();
            if (list.RemoveAll(keys.Contains) > 0) await SaveAsync(list, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<string>> LoadAsync(CancellationToken ct)
    {
        if (_entries is not null) return _entries;
        if (!File.Exists(_path)) return _entries = [];
        try
        {
            var stream = File.OpenRead(_path);
            await using (stream.ConfigureAwait(false))
            {
                return _entries = await JsonSerializer.DeserializeAsync<List<string>>(stream, cancellationToken: ct).ConfigureAwait(false) ?? [];
            }
        }
        catch (JsonException)
        {
            return _entries = []; // a corrupt log only means nothing can be edited/deleted (fail closed)
        }
    }

    private async Task SaveAsync(List<string> list, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(tmp, options);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, list, cancellationToken: ct).ConfigureAwait(false);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
