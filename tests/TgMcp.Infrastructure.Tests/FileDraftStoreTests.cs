using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TgMcp.Application.Telegram;
using TgMcp.Infrastructure.Telegram;

namespace TgMcp.Infrastructure.Tests;

public class FileDraftStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Saves_lists_gets_and_deletes_with_owner_only_permissions()
    {
        using var dir = new TempDir();
        var store = new FileDraftStore(Options.Create(new TelegramOptions { DataDir = dir.Path }), new FakeTimeProvider());
        var msg = new OutgoingMessage(-1001234567890, "hello\nworld", 5, 3, true);

        var d = await store.SaveAsync("Work", msg, Ct);
        Assert.Matches("^[0-9a-f]{8}$", d.Id);
        var path = Path.Combine(dir.Path, "drafts", d.Id + ".json");
        Assert.True(File.Exists(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(dir.Path, "drafts")));
        }

        var loaded = await store.GetAsync(d.Id, Ct);
        Assert.Equal(msg, loaded!.Message);
        Assert.Equal("Work", loaded.ChatTitle);
        Assert.Single(await store.ListAsync(Ct));
        Assert.True(await store.DeleteAsync(d.Id, Ct));
        Assert.False(await store.DeleteAsync(d.Id, Ct));
        Assert.Empty(await store.ListAsync(Ct));
    }

    [Fact]
    public async Task Join_drafts_round_trip_and_old_message_drafts_still_load()
    {
        using var dir = new TempDir();
        var store = new FileDraftStore(Options.Create(new TelegramOptions { DataDir = dir.Path }), new FakeTimeProvider());
        var join = new TgMcp.Application.Drafts.PendingJoin(TgMcp.Domain.JoinTargetKind.Invite, "AbCdEfGh12", null, "supergroup", 40);
        var d = await store.SaveJoinAsync("Private Group", join, Ct);
        var loaded = await store.GetAsync(d.Id, Ct);
        Assert.Equal("join", loaded!.Kind);
        Assert.Equal(join, loaded.Join);
        Assert.Null(loaded.Message);

        await File.WriteAllTextAsync(Path.Combine(dir.Path, "drafts", "0a0b0c0d.json"),
            """{"Id":"0a0b0c0d","CreatedUtc":"2030-01-01T00:00:00+00:00","ChatTitle":"T","Message":{"ChatId":-1001234567890,"Text":"hi","ReplyToMessageId":0,"TopicId":null,"DisableNotification":false}}""", Ct);
        var old = await store.GetAsync("0a0b0c0d", Ct);
        Assert.Equal("message", old!.Kind);
        Assert.Equal("hi", old.Message!.Text);
    }

    [Fact]
    public async Task Reaction_drafts_round_trip()
    {
        using var dir = new TempDir();
        var store = new FileDraftStore(Options.Create(new TelegramOptions { DataDir = dir.Path }), new FakeTimeProvider());
        var d = await store.SaveReactionAsync("Work", new TgMcp.Application.Drafts.PendingReaction(-1001234567890, 42, "👍"), Ct);
        var loaded = await store.GetAsync(d.Id, Ct);
        Assert.Equal("reaction", loaded!.Kind);
        Assert.Equal("👍", loaded.Reaction!.Emoji);
        Assert.Equal(42, loaded.Reaction.MessageId);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("ABCDEF12")]
    [InlineData("1234567")]
    [InlineData("")]
    public async Task Rejects_malformed_ids(string id)
    {
        using var dir = new TempDir();
        var store = new FileDraftStore(Options.Create(new TelegramOptions { DataDir = dir.Path }), new FakeTimeProvider());
        Assert.Null(await store.GetAsync(id, Ct));
        Assert.False(await store.DeleteAsync(id, Ct));
    }

    [Fact]
    public void Lock_errors_are_explained()
    {
        Assert.Contains("in use by another tgmcp process", TdTelegramClient.DatabaseProblem("Can't lock file \"/data/tdlib/td.binlog\", because it is already in use; check for another program instance running"), StringComparison.Ordinal);
        Assert.StartsWith("TDLib could not open its database", TdTelegramClient.DatabaseProblem("Wrong password"), StringComparison.Ordinal);
    }
}
