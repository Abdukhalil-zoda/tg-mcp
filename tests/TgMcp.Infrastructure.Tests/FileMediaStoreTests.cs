using Microsoft.Extensions.Options;
using TgMcp.Infrastructure.Telegram;

namespace TgMcp.Infrastructure.Tests;

public class FileMediaStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stores_in_configured_media_dir_and_maps_host_path()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "source.bin");
        await File.WriteAllTextAsync(src, "data", Ct);
        var media = Path.Combine(dir.Path, "media");
        var store = new FileMediaStore(Options.Create(new TelegramOptions { DataDir = "/data", MediaDir = media, MediaHostDir = "/var/lib/tgmcp/media" }));

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
        var store = new FileMediaStore(Options.Create(new TelegramOptions { DataDir = dir.Path }));
        var stored = await store.StoreAsync(src, 1, 2, null, Ct);
        Assert.Equal(Path.Combine(dir.Path, "media", "1_2_s"), stored.Path);
        Assert.Null(stored.HostPath);
    }
}
