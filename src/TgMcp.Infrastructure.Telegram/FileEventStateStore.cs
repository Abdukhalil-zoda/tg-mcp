using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TgMcp.Application.Events;

namespace TgMcp.Infrastructure.Telegram;

/// <summary>Keeps the last delivered message id per chat in <c>{DataDir}/events-state.json</c> (atomic replace, owner-only).</summary>
public sealed class FileEventStateStore : IEventStateStore
{
    private readonly string _path;

    /// <summary>Creates the store.</summary>
    public FileEventStateStore(IOptions<TelegramOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = Path.Combine(options.Value.DataDir, "events-state.json");
    }

    /// <inheritdoc />
    public async Task<Dictionary<long, long>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var stream = File.OpenRead(_path);
            await using (stream.ConfigureAwait(false))
            {
                var raw = await JsonSerializer.DeserializeAsync<Dictionary<string, long>>(stream, cancellationToken: ct).ConfigureAwait(false) ?? [];
                return raw.Where(kv => long.TryParse(kv.Key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                    .ToDictionary(kv => long.Parse(kv.Key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), kv => kv.Value);
            }
        }
        catch (JsonException)
        {
            return []; // a corrupt state only disables de-duplication for catch-up
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(IReadOnlyDictionary<long, long> lastDelivered, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lastDelivered);
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(tmp, options);
        await using (stream.ConfigureAwait(false))
        {
            var data = lastDelivered.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value);
            await JsonSerializer.SerializeAsync(stream, data, cancellationToken: ct).ConfigureAwait(false);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
