using TgMcp.Infrastructure.Telegram;
using static TdLib.TdApi;

namespace TgMcp.Infrastructure.Tests;

public class InboundRouterTests
{
    private static Message M(long id, MessageSendingState? state = null) => new() { Id = id, ChatId = -1001234567890, IsOutgoing = state is not null, SendingState = state };

    [Fact]
    public void Incoming_messages_pass_through()
    {
        var m = M(10);
        Assert.Same(m, InboundRouter.ForEvents(new Update.UpdateNewMessage { Message = m }));
    }

    [Fact]
    public void Pending_outgoing_messages_wait_for_the_final_id()
    {
        var temp = M(1_000_000_123, new MessageSendingState.MessageSendingStatePending());
        Assert.Null(InboundRouter.ForEvents(new Update.UpdateNewMessage { Message = temp }));

        var final = M(4242);
        Assert.Same(final, InboundRouter.ForEvents(new Update.UpdateMessageSendSucceeded { Message = final, OldMessageId = temp.Id }));
    }

    [Fact]
    public void Failed_sends_and_other_updates_are_ignored()
    {
        var failed = M(1_000_000_124, new MessageSendingState.MessageSendingStateFailed());
        Assert.Null(InboundRouter.ForEvents(new Update.UpdateNewMessage { Message = failed }));
        Assert.Null(InboundRouter.ForEvents(new Update.UpdateMessageSendFailed { Message = failed, OldMessageId = 1, Error = new Error { Code = 400, Message = "x" } }));
        Assert.Null(InboundRouter.ForEvents(new Update.UpdateChatFolders()));
    }

    [Fact]
    public void Messages_sent_from_other_devices_are_already_final()
    {
        var mine = M(4300);
        mine.IsOutgoing = true;
        Assert.Same(mine, InboundRouter.ForEvents(new Update.UpdateNewMessage { Message = mine }));
    }
}
