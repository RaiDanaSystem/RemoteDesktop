using RemoteSupport.Shared.Clipboard;
using RemoteSupport.Shared.Transport.Messages;

namespace RemoteSupport.Server.Tests.Transport;

public class ClipboardTransportTests
{
    [Fact]
    public void SerializeDeserialize_Text_PreservesContent()
    {
        var transport = new ClipboardTransport
        {
            Type = ClipboardMessageType.TextSync,
            Text = "Hello, World!",
            SourceId = "session-123"
        };

        var serialized = transport.Serialize();
        var deserialized = ClipboardTransport.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(ClipboardMessageType.TextSync, deserialized.Type);
        Assert.Equal("Hello, World!", deserialized.Text);
        Assert.Equal("session-123", deserialized.SourceId);
    }

    [Fact]
    public void ToClipboardMessage_RoundTrip_PreservesText()
    {
        var transport = new ClipboardTransport
        {
            Type = ClipboardMessageType.TextSync,
            Text = "Test clipboard content"
        };

        var msg = transport.ToClipboardMessage();

        Assert.Equal(ClipboardMessageType.TextSync, msg.Type);
        Assert.Equal("Test clipboard content", msg.Text);
    }

    [Fact]
    public void Deserialize_InvalidData_ReturnsNull()
    {
        var result = ClipboardTransport.Deserialize(new byte[] { 0, 1, 2 });
        Assert.Null(result);
    }
}
