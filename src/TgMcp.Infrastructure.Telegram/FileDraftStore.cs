using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TgMcp.Application.Drafts;
using TgMcp.Application.Telegram;

namespace TgMcp.Infrastructure.Telegram;

/// <summary>
/// Stores each draft as <c>{DataDir}/drafts/{id}.json</c>; the directory is 0700 and files are 0600
/// (owner only). Draft text is message content and is never logged.
/// </summary>
public sealed class FileDraftStore : IDraftStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _dir;
    private readonly TimeProvider _time;

    /// <summary>Creates the store.</summary>
    public FileDraftStore(IOptions<TelegramOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dir = Path.Combine(options.Value.DataDir, "drafts");
        _time = time;
    }

    /// <inheritdoc />
    public Task<Draft> SaveAsync(string chatTitle, OutgoingMessage message, CancellationToken ct) =>
        WriteAsync(new Draft(NewId(), _time.GetUtcNow(), chatTitle, message), ct);

    /// <inheritdoc />
    public Task<Draft> SaveJoinAsync(string chatTitle, PendingJoin join, CancellationToken ct) =>
        WriteAsync(new Draft(NewId(), _time.GetUtcNow(), chatTitle, null, Draft.JoinKind, join), ct);

    /// <inheritdoc />
    public Task<Draft> SavePhoneAsync(PendingPhoneMessage message, CancellationToken ct) =>
        WriteAsync(new Draft(NewId(), _time.GetUtcNow(), message.ContactName, null, Draft.PhoneKind, Phone: message), ct);

    /// <inheritdoc />
    public Task<Draft> SaveEditAsync(string chatTitle, PendingEdit edit, CancellationToken ct) =>
        WriteAsync(new Draft(NewId(), _time.GetUtcNow(), chatTitle, null, Draft.EditKind, Edit: edit), ct);

    /// <inheritdoc />
    public Task<Draft> SaveDeleteAsync(string chatTitle, PendingDelete delete, CancellationToken ct) =>
        WriteAsync(new Draft(NewId(), _time.GetUtcNow(), chatTitle, null, Draft.DeleteKind, Delete: delete), ct);

    /// <inheritdoc />
    public Task<Draft> SaveForwardAsync(string chatTitle, PendingForward forward, CancellationToken ct) =>
        WriteAsync(new Draft(NewId(), _time.GetUtcNow(), chatTitle, null, Draft.ForwardKind, Forward: forward), ct);

    /// <inheritdoc />
    public Task<Draft> SaveReactionAsync(string chatTitle, PendingReaction reaction, CancellationToken ct) =>
        WriteAsync(new Draft(NewId(), _time.GetUtcNow(), chatTitle, null, Draft.ReactionKind, Reaction: reaction), ct);

    private async Task<Draft> WriteAsync(Draft draft, CancellationToken ct)
    {
        EnsureDir();
        var path = PathOf(draft.Id);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, options);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, draft, Json, ct).ConfigureAwait(false);
        }
        return draft;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Draft>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_dir)) return [];
        var list = new List<Draft>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            if (await Read(file, ct).ConfigureAwait(false) is Draft d) list.Add(d);
        }
        return [.. list.OrderBy(d => d.CreatedUtc)];
    }

    /// <inheritdoc />
    public Task<Draft?> GetAsync(string id, CancellationToken ct) =>
        IsValidId(id) ? Read(PathOf(id), ct) : Task.FromResult<Draft?>(null);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        if (!IsValidId(id) || !File.Exists(PathOf(id))) return Task.FromResult(false);
        File.Delete(PathOf(id));
        return Task.FromResult(true);
    }

    private void EnsureDir()
    {
        if (Directory.Exists(_dir)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_dir);
        else Directory.CreateDirectory(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string PathOf(string id) => Path.Combine(_dir, id + ".json");

    private static async Task<Draft?> Read(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                return await JsonSerializer.DeserializeAsync<Draft>(stream, Json, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>Draft ids are 8 lowercase hex characters; anything else is rejected (no path tricks).</summary>
    internal static bool IsValidId(string id) =>
        id is { Length: 8 } && id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
}
