using Microsoft.Extensions.Options;
using TgMcp.Infrastructure.Telegram;

namespace TgMcp.Infrastructure.Tests;

public class FileEventStateStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Round_trips_last_delivered_ids()
    {
        using var dir = new TempDir();
        var store = new FileEventStateStore(Options.Create(new TelegramOptions { DataDir = dir.Path }));
        Assert.Empty(await store.LoadAsync(Ct));
        await store.SaveAsync(new Dictionary<long, long> { [-1001234567890] = 42, [111] = 7 }, Ct);
        var loaded = await store.LoadAsync(Ct);
        Assert.Equal(42, loaded[-1001234567890]);
        Assert.Equal(7, loaded[111]);
        var path = Path.Combine(dir.Path, "events-state.json");
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Corrupt_state_loads_as_empty()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "events-state.json"), "{ not json", Ct);
        Assert.Empty(await new FileEventStateStore(Options.Create(new TelegramOptions { DataDir = dir.Path })).LoadAsync(Ct));
    }
}
