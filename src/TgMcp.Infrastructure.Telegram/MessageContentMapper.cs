using TgMcp.Application.Telegram;
using static TdLib.TdApi;
using static TdLib.TdApi.MessageContent;

namespace TgMcp.Infrastructure.Telegram;

/// <summary>Extracts text/caption and the downloadable file from TDLib message content.</summary>
public static class MessageContentMapper
{
    /// <summary>Text (or a "[kind]" placeholder) and media of <paramref name="content"/>.</summary>
    public static (string Text, TgMedia? Media) Map(MessageContent? content) => content switch
    {
        MessageText t => (t.Text?.Text ?? "", null),
        MessagePhoto p => (Caption(p.Caption, "photo"), Photo(p.Photo)),
        MessageVideo v => (Caption(v.Caption, "video"), File(v.Video?.Video_, "video", v.Video?.FileName, v.Video?.MimeType)),
        MessageDocument d => (Caption(d.Caption, "document"), File(d.Document?.Document_, "document", d.Document?.FileName, d.Document?.MimeType)),
        MessageAudio a => (Caption(a.Caption, "audio"), File(a.Audio?.Audio_, "audio", a.Audio?.FileName, a.Audio?.MimeType)),
        MessageVoiceNote n => (Caption(n.Caption, "voice"), File(n.VoiceNote?.Voice, "voice", null, n.VoiceNote?.MimeType)),
        MessageAnimation an => (Caption(an.Caption, "animation"), File(an.Animation?.Animation_, "animation", an.Animation?.FileName, an.Animation?.MimeType)),
        MessageVideoNote vn => ("[video note]", File(vn.VideoNote?.Video, "video_note", null, "video/mp4")),
        MessageSticker => ("[sticker]", null),
        null => ("", null),
        _ => ($"[{Placeholder(content)}]", null),
    };

    private static string Caption(FormattedText? caption, string kind) =>
        string.IsNullOrEmpty(caption?.Text) ? $"[{kind}]" : caption.Text;

    private static TgMedia? Photo(Photo? photo)
    {
        var best = photo?.Sizes?.OrderByDescending(s => (long)s.Width * s.Height).FirstOrDefault();
        return File(best?.Photo, "photo", null, "image/jpeg");
    }

    private static TgMedia? File(TdLib.TdApi.File? f, string kind, string? name, string? mime) =>
        f is null ? null : new TgMedia(f.Id, f.Size > 0 ? f.Size : f.ExpectedSize, kind, string.IsNullOrEmpty(name) ? null : name, mime);

    private static string Placeholder(MessageContent c)
    {
        var n = c.GetType().Name;
        return (n.StartsWith("Message", StringComparison.Ordinal) ? n[7..] : n).ToLowerInvariant();
    }
}
