using TgMcp.Application.Telegram;
using TgMcp.Domain;

namespace TgMcp.Application.Tools;

/// <summary>download_media: downloads a message's file. Needs read + media_download; size capped by limits.maxMediaMb.</summary>
public sealed class DownloadMediaTool
{
    private readonly ToolContext _c;
    private readonly IMediaStore _store;

    /// <summary>Creates the tool.</summary>
    public DownloadMediaTool(ToolContext context, IMediaStore store)
    {
        _c = context;
        _store = store;
    }

    /// <summary>Runs the tool.</summary>
    public Task<ToolResult> RunAsync(string chat, long messageId, CancellationToken ct) =>
        _c.Runner.RunAsync("download_media", [Need.Read, Need.MediaDownload], async token =>
        {
            var target = await _c.Chats.ResolveAsync(chat, token).ConfigureAwait(false);
            var d = _c.Policy.Check(target.Info, Need.Read, Need.MediaDownload);
            if (!d.Allowed) return _c.Deny(d, target);
            var m = await _c.Telegram.GetMessageAsync(target.Id, messageId, token).ConfigureAwait(false);
            if (m is null) return ToolOutcome.Failed("message not found", target.Id, d.Rule);
            if (!d.DateOk(m.Date, _c.Time.GetUtcNow())) return _c.Deny(d.Deny("older than SinceDays"), target, DenialKind.TooOld);
            if (!d.TopicOk(m.TopicId)) return _c.Deny(d.Deny("message is outside allowed topics"), target, DenialKind.Topic, m.TopicId);
            if (m.Media is not TgMedia media) return ToolOutcome.Failed("message has no media", target.Id, d.Rule);

            var cap = _c.Policy.Config.MaxMediaMb * 1024L * 1024L;
            if (media.Size <= 0) return ToolOutcome.Refused("file size is unknown, so it is refused (limits.maxMediaMb)", target.Id, d.Rule);
            if (media.Size > cap) return ToolOutcome.Refused($"file is {media.Size} bytes, over limits.maxMediaMb={_c.Policy.Config.MaxMediaMb}", target.Id, d.Rule);

            _store.Cleanup(_c.Policy.Config.MediaRetentionDays);
            var file = await _c.Telegram.DownloadFileAsync(media.FileId, token).ConfigureAwait(false);
            if (!file.Completed || file.LocalPath is null) return ToolOutcome.Failed("download did not complete", target.Id, d.Rule);
            if (file.Size > cap) return ToolOutcome.Refused("downloaded file is over limits.maxMediaMb", target.Id, d.Rule);
            var stored = await _store.StoreAsync(file.LocalPath, target.Id, m.Id, media.FileName, token).ConfigureAwait(false);
            var payload = new
            {
                note = ToolResult.ThirdPartyNote,
                chat = target.Id, message_id = m.Id, path = stored.Path, host_path = stored.HostPath, size = file.Size, kind = media.Kind,
                file_name = media.FileName, mime_type = media.MimeType,
            };
            return ToolOutcome.Ok(payload, target.Id, 1, d);
        }, ct);
}
