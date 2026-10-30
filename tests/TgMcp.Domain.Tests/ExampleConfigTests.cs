namespace TgMcp.Domain.Tests;

/// <summary>The shipped tgmcp.env.example must stay valid and keep its safety properties.</summary>
public class ExampleConfigTests
{
    private static PolicyConfig LoadExample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tgmcp.env.example"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var pairs = File.ReadAllLines(Path.Combine(dir.FullName, "tgmcp.env.example"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(PolicyTests.Split)
            .ToArray();
        return PolicyConfig.FromPairs(pairs);
    }

    [Fact]
    public void Example_is_valid_and_denies_bots_and_service_notifications()
    {
        var c = LoadExample();
        Assert.True(c.IsValid, string.Join("; ", c.Errors));
        var p = new PolicyEngine(c);
        p.SetFolders(new Dictionary<string, IReadOnlySet<long>> { ["Finance"] = new HashSet<long>() });
        Assert.False(p.Check(PolicyTests.Bank, Need.Read).Allowed);
        Assert.False(p.Check(new ChatInfo(777000, "Telegram", (string?)null, ChatKind.Private), Need.Read).Allowed);
        Assert.True(p.Check(new ChatInfo(-1001234567890, "Work", (string?)null, ChatKind.Group), Need.Read, Need.Search).Allowed);
    }
}
