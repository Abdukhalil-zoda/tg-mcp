using System.Globalization;
using TgMcp.Application.Telegram;
using static TdLib.TdApi;
using static TdLib.TdApi.MessageContent;

namespace TgMcp.Infrastructure.Telegram;

/// <summary>
/// Extracts text and the downloadable file from TDLib message content. Media is described in brackets
/// (e.g. <c>[photo]</c>, <c>[document report.pdf]</c>, <c>[voice 12s]</c>), followed by the caption; nothing is downloaded.
/// </summary>
public static class MessageContentMapper
{
    /// <summary>Text (description + caption for media) and media of <paramref name="content"/>.</summary>
    public static (string Text, TgMedia? Media) Map(MessageContent? content) => content switch
    {
        MessageText t => (t.Text?.Text ?? "", null),
        MessagePhoto p => (With("[photo]", p.Caption), Photo(p.Photo)),
        MessageVideo v => (With($"[video{Seconds(v.Video?.Duration)}]", v.Caption), File(v.Video?.Video_, "video", v.Video?.FileName, v.Video?.MimeType)),
        MessageDocument d => (With($"[document{Name(d.Document?.FileName)}]", d.Caption), File(d.Document?.Document_, "document", d.Document?.FileName, d.Document?.MimeType)),
        MessageAudio a => (With($"[audio{Name(AudioName(a.Audio))}{Seconds(a.Audio?.Duration)}]", a.Caption), File(a.Audio?.Audio_, "audio", a.Audio?.FileName, a.Audio?.MimeType)),
        MessageVoiceNote n => (With($"[voice{Seconds(n.VoiceNote?.Duration)}]", n.Caption), File(n.VoiceNote?.Voice, "voice", null, n.VoiceNote?.MimeType)),
        MessageAnimation an => (With("[animation]", an.Caption), File(an.Animation?.Animation_, "animation", an.Animation?.FileName, an.Animation?.MimeType)),
        MessageVideoNote vn => ($"[video note{Seconds(vn.VideoNote?.Duration)}]", File(vn.VideoNote?.Video, "video_note", null, "video/mp4")),
        MessageSticker s => (string.IsNullOrEmpty(s.Sticker?.Emoji) ? "[sticker]" : $"[sticker {s.Sticker.Emoji}]", null),
        null => ("", null),
        _ => ($"[{Placeholder(content)}]", null),
    };

    /// <summary>"[kind]" or "[kind] caption".</summary>
    private static string With(string description, FormattedText? caption) =>
        string.IsNullOrEmpty(caption?.Text) ? description : $"{description} {caption.Text}";

    private static string Seconds(int? duration) => duration is > 0 ? " " + duration.Value.ToString(CultureInfo.InvariantCulture) + "s" : "";

    private static string Name(string? name) => string.IsNullOrEmpty(name) ? "" : " " + name;

    private static string? AudioName(Audio? a) =>
        a is null ? null : !string.IsNullOrEmpty(a.Title) ? (string.IsNullOrEmpty(a.Performer) ? a.Title : $"{a.Performer} - {a.Title}") : a.FileName;

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
