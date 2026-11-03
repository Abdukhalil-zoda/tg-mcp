using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TgMcp.Infrastructure.Telegram;

namespace TgMcp.Infrastructure.Tests;

public class FileExportStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2030, 1, 15, 12, 30, 45, TimeSpan.Zero);

    [Fact]
    public async Task Writes_an_owner_only_file_atomically()
    {
        using var dir = new TempDir();
        using var store = new FileExportStore(Options.Create(new TelegramOptions { DataDir = dir.Path }), new FakeTimeProvider(Now));
        var stored = await store.WriteAsync("read_new", "{\"total\":1}", Ct);
        Assert.Equal("read_new-20300115T123045Z.json", stored.FileName);
        Assert.Equal(Path.Combine(dir.Path, "exports", stored.FileName), stored.Path);
        Assert.Equal("{\"total\":1}", await File.ReadAllTextAsync(stored.Path, Ct));
        Assert.Single(Directory.GetFiles(store.ExportDir)); // no temp file left behind
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(stored.Path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(store.ExportDir));
        }

        var second = await store.WriteAsync("read_new", "{}", Ct); // same second: never overwrites
        Assert.Equal("read_new-20300115T123045Z-1.json", second.FileName);
        Assert.Equal("{\"total\":1}", await File.ReadAllTextAsync(stored.Path, Ct));
    }

    [Fact]
    public async Task Deletes_its_files_older_than_two_days()
    {
        using var dir = new TempDir();
        var exports = Path.Combine(dir.Path, "exports");
        Directory.CreateDirectory(exports);
        var old = Path.Combine(exports, "read_new-20300112T000000Z.json");
        var recent = Path.Combine(exports, "read_new-20300114T000000Z.json");
        var foreign = Path.Combine(exports, "notes.json");
        var unstamped = Path.Combine(exports, "read_new-manual.json");
        foreach (var f in new[] { old, recent, foreign, unstamped }) await File.WriteAllTextAsync(f, "{}", Ct);
        File.SetLastWriteTimeUtc(unstamped, Now.UtcDateTime.AddDays(-3)); // no stamp in the name: last write time decides
        File.SetLastWriteTimeUtc(foreign, Now.UtcDateTime.AddDays(-30));

        using var store = new FileExportStore(Options.Create(new TelegramOptions { DataDir = dir.Path }), new FakeTimeProvider(Now));
        var stored = await store.WriteAsync("read_new", "{}", Ct);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign));
        Assert.False(File.Exists(unstamped));
        Assert.True(File.Exists(stored.Path));
    }

    [Fact]
    public async Task Rejects_unsafe_prefixes()
    {
        using var dir = new TempDir();
        using var store = new FileExportStore(Options.Create(new TelegramOptions { DataDir = dir.Path }), new FakeTimeProvider(Now));
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("../x", "{}", Ct));
    }
}
