using TgMcp.Application.Telegram;

namespace TgMcp.Application.Tests;

public class MediaPathsTests
{
    [Theory]
    [InlineData("/media/1_2_a.pdf", "/media", "/var/lib/tgmcp/media", "/var/lib/tgmcp/media/1_2_a.pdf")]
    [InlineData("/media/1_2_a.pdf", "/media/", "/srv/m/", "/srv/m/1_2_a.pdf")]
    [InlineData("/media/sub/x.bin", "/media", "/srv/m", "/srv/m/sub/x.bin")]
    [InlineData("/media/1_2_a.pdf", "/media", null, null)]
    [InlineData("/media/1_2_a.pdf", "/media", "", null)]
    [InlineData("/data/files/x", "/media", "/srv/m", null)]
    [InlineData("/media", "/media", "/srv/m", null)]
    public void Maps_container_paths_to_host_paths(string path, string mediaDir, string? hostDir, string? expected) =>
        Assert.Equal(expected, MediaPaths.ToHostPath(path, mediaDir, hostDir));
}
