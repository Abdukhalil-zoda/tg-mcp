using Microsoft.Extensions.Options;
using TgMcp.Infrastructure.Telegram;

namespace TgMcp.Infrastructure.Tests;

public class FileSentLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Records_survive_restarts_and_removals_stick()
    {
        using var dir = new TempDir();
        var options = Options.Create(new TelegramOptions { DataDir = dir.Path });
        using (var log = new FileSentLog(options))
        {
            await log.RecordAsync(-1001234567890, 42, Ct);
            await log.RecordAsync(-1001234567890, 43, Ct);
            await log.RemoveAsync(-1001234567890, [43], Ct);
        }
        using var reopened = new FileSentLog(options);
        Assert.True(await reopened.ContainsAsync(-1001234567890, 42, Ct));
        Assert.False(await reopened.ContainsAsync(-1001234567890, 43, Ct));
        Assert.False(await reopened.ContainsAsync(111, 42, Ct));
        var path = Path.Combine(dir.Path, "sent-log.json");
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.DoesNotContain("text", await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keeps_at_most_capacity_entries()
    {
        using var dir = new TempDir();
        using var log = new FileSentLog(Options.Create(new TelegramOptions { DataDir = dir.Path }));
        for (var i = 1; i <= FileSentLog.Capacity + 5; i++) await log.RecordAsync(1, i, Ct);
        Assert.False(await log.ContainsAsync(1, 1, Ct));
        Assert.True(await log.ContainsAsync(1, FileSentLog.Capacity + 5, Ct));
    }
}
