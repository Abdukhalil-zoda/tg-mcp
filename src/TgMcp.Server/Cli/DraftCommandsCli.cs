using Microsoft.Extensions.DependencyInjection;
using TgMcp.Application.Drafts;
using TgMcp.Server.Hosting;

namespace TgMcp.Server.Cli;

/// <summary>Owner CLI for drafts: <c>drafts</c>, <c>approve &lt;id&gt;</c>, <c>reject &lt;id&gt;</c>.</summary>
public static class DraftCommandsCli
{
    /// <summary>Runs one draft command; returns the process exit code.</summary>
    public static async Task<int> RunAsync(string command, string? id, TextWriter output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        var builder = Composition.CreateBuilder([]);
        builder.Services.AddTelegramTools();
        using var host = builder.Build();
        {
            var drafts = host.Services.GetRequiredService<DraftCommands>();
            switch (command)
            {
                case "drafts":
                    var list = await drafts.ListAsync(ct).ConfigureAwait(false);
                    if (list.Count == 0) output.WriteLine("no drafts");
                    foreach (var d in list)
                    {
                        var preview = d.Message.Text.ReplaceLineEndings(" ");
                        if (preview.Length > 80) preview = preview[..80] + "...";
                        output.WriteLine($"{d.Id}  {d.CreatedUtc.UtcDateTime:yyyy-MM-dd HH:mm}Z  chat {d.Message.ChatId} \"{d.ChatTitle}\"  {preview}");
                    }
                    return 0;
                case "approve" when id is not null:
                    var result = await drafts.ApproveAsync(id, ct).ConfigureAwait(false);
                    output.WriteLine(result.Text);
                    return result.IsError ? 1 : 0;
                case "reject" when id is not null:
                    var removed = await drafts.RejectAsync(id, ct).ConfigureAwait(false);
                    output.WriteLine(removed ? $"draft {id} rejected (deleted, nothing sent)" : $"no draft with id {id}");
                    return removed ? 0 : 1;
                default:
                    output.WriteLine($"usage: tgmcp {command} <draft-id>");
                    return 2;
            }
        }
    }
}
