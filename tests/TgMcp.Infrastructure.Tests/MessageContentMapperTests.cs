using TgMcp.Infrastructure.Telegram;
using static TdLib.TdApi;
using static TdLib.TdApi.MessageContent;

namespace TgMcp.Infrastructure.Tests;

public class MessageContentMapperTests
{
    private static TdLib.TdApi.File F(int id, long size) => new() { Id = id, Size = size };

    private static FormattedText T(string s) => new() { Text = s, Entities = [] };

    [Fact]
    public void Describes_media_without_downloading()
    {
        Assert.Equal("hello", MessageContentMapper.Map(new MessageText { Text = T("hello") }).Text);
        Assert.Equal("[photo]", MessageContentMapper.Map(new MessagePhoto { Photo = new Photo { Sizes = [new PhotoSize { Photo = F(1, 10), Width = 2, Height = 2 }] }, Caption = T("") }).Text);
        Assert.Equal("[photo] look", MessageContentMapper.Map(new MessagePhoto { Photo = new Photo { Sizes = [] }, Caption = T("look") }).Text);

        var doc = MessageContentMapper.Map(new MessageDocument { Document = new Document { FileName = "report.pdf", MimeType = "application/pdf", Document_ = F(7, 1234) }, Caption = T("Q3") });
        Assert.Equal("[document report.pdf] Q3", doc.Text);
        Assert.Equal(7, doc.Media!.FileId);
        Assert.Equal(1234, doc.Media.Size);

        Assert.Equal("[voice 12s]", MessageContentMapper.Map(new MessageVoiceNote { VoiceNote = new VoiceNote { Duration = 12, Voice = F(2, 5) }, Caption = T("") }).Text);
        Assert.Equal("[video 30s] clip", MessageContentMapper.Map(new MessageVideo { Video = new Video { Duration = 30, Video_ = F(3, 5) }, Caption = T("clip") }).Text);
        Assert.Equal("[audio Band - Song 200s]", MessageContentMapper.Map(new MessageAudio { Audio = new Audio { Title = "Song", Performer = "Band", Duration = 200, Audio_ = F(4, 5) }, Caption = T("") }).Text);
        Assert.Equal("[video note 9s]", MessageContentMapper.Map(new MessageVideoNote { VideoNote = new VideoNote { Duration = 9, Video = F(5, 5) } }).Text);
        Assert.Equal("[sticker 👍]", MessageContentMapper.Map(new MessageSticker { Sticker = new Sticker { Emoji = "👍" } }).Text);
        Assert.Equal("[location]", MessageContentMapper.Map(new MessageLocation()).Text);
    }
}
