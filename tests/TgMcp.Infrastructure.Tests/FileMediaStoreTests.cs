using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TgMcp.Infrastructure.Telegram;

namespace TgMcp.Infrastructure.Tests;

public class FileMediaStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static FileMediaStore Store(TelegramOptions o) =>
        new(Options.Create(o), new FakeTimeProvider(Now), NullLogger<FileMediaStore>.Instance);

    [Fact]
    public void Cleanup_deletes_only_files_older_than_retention()
    {
        using var dir = new TempDir();
        var media = Path.Combine(dir.Path, "media");
        Directory.CreateDirectory(media);
        string Make(string name, double daysOld)
        {
            var p = Path.Combine(media, name);
            File.WriteAllText(p, "x");
            File.SetLastWriteTimeUtc(p, Now.UtcDateTime.AddDays(-daysOld));
            return p;
        }
        var old = Make("old.bin", 8);
        var fresh = Make("fresh.bin", 6);
        var store = Store(new TelegramOptions { MediaDir = media });

        Assert.Equal(0, store.Cleanup(0)); // disabled
        Assert.True(File.Exists(old));
        Assert.Equal(1, store.Cleanup(7));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Cleanup_without_media_dir_is_a_no_op() =>
        Assert.Equal(0, Store(new TelegramOptions { MediaDir = "/nonexistent-tgmcp-media" }).Cleanup(7));

    [Fact]
    public async Task Stores_in_configured_media_dir_and_maps_host_path()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "source.bin");
        await File.WriteAllTextAsync(src, "data", Ct);
        var media = Path.Combine(dir.Path, "media");
        var store = Store(new TelegramOptions { DataDir = "/data", MediaDir = media, MediaHostDir = "/var/lib/tgmcp/media" });

        var stored = await store.StoreAsync(src, -1001234567890, 42, "../../etc/passwd report.pdf", Ct);

        Assert.Equal(media, Path.GetDirectoryName(stored.Path));
        Assert.Equal("-1001234567890_42__.._etc_passwd_report.pdf", Path.GetFileName(stored.Path));
        Assert.Equal("data", await File.ReadAllTextAsync(stored.Path, Ct));
        Assert.Equal("/var/lib/tgmcp/media/" + Path.GetFileName(stored.Path), stored.HostPath);
    }

    [Fact]
    public async Task Defaults_to_data_dir_media_without_host_path()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "s");
        await File.WriteAllTextAsync(src, "x", Ct);
        var store = Store(new TelegramOptions { DataDir = dir.Path });
        var stored = await store.StoreAsync(src, 1, 2, null, Ct);
        Assert.Equal(Path.Combine(dir.Path, "media", "1_2_s"), stored.Path);
        Assert.Null(stored.HostPath);
    }
}
