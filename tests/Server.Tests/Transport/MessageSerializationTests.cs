using RemoteSupport.Shared.Transport;
using RemoteSupport.Shared.Transport.Messages;

namespace RemoteSupport.Server.Tests.Transport;

public class MessageSerializationTests
{
    [Fact]
    public void TransportEnvelope_SingleWrap_SendAsync()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var envelope = new TransportEnvelope
        {
            MessageType = TransportMessageType.ScreenFrame,
            Payload = payload
        };

        var serialized = envelope.Serialize();

        var deserialized = TransportEnvelope.Deserialize(serialized);
        Assert.NotNull(deserialized);
        Assert.Equal(TransportMessageType.ScreenFrame, deserialized.MessageType);
        Assert.Equal(payload, deserialized.Payload.ToArray());
    }

    [Fact]
    public void ScreenFrameTransport_SerializeDeserialize_RoundTrip()
    {
        var original = new ScreenFrameTransport
        {
            FrameId = 42,
            TimestampMs = 1234567890,
            Width = 1920,
            Height = 1080,
            Format = RemoteSupport.Shared.ScreenStreaming.FrameFormat.JPEG,
            MonitorIndex = 0,
            MonitorCount = 2,
            FramePayload = new byte[] { 10, 20, 30, 40 }
        };

        var serialized = original.Serialize();
        var deserialized = ScreenFrameTransport.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(42u, deserialized.FrameId);
        Assert.Equal(1234567890, deserialized.TimestampMs);
        Assert.Equal(1920, deserialized.Width);
        Assert.Equal(1080, deserialized.Height);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, deserialized.FramePayload.ToArray());
    }

    [Fact]
    public void InputTransport_SerializeDeserialize_RoundTrip()
    {
        var original = new InputTransport
        {
            Type = RemoteSupport.Shared.RemoteInput.InputEventType.MouseMove,
            TimestampMs = 1234567890,
            MouseX = 500,
            MouseY = 300
        };

        var serialized = original.Serialize();
        var deserialized = InputTransport.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(RemoteSupport.Shared.RemoteInput.InputEventType.MouseMove, deserialized.Type);
        Assert.Equal(500, deserialized.MouseX);
        Assert.Equal(300, deserialized.MouseY);
    }

    [Fact]
    public void ControlMessage_SerializeDeserialize_RoundTrip()
    {
        var original = new ControlMessage
        {
            Action = ControlAction.Disconnect,
            Reason = "Test disconnect"
        };

        var serialized = original.Serialize();
        var deserialized = ControlMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(ControlAction.Disconnect, deserialized.Action);
        Assert.Equal("Test disconnect", deserialized.Reason);
    }

    [Fact]
    public void ClipboardTransport_SerializeDeserialize_RoundTrip()
    {
        var original = new ClipboardTransport
        {
            Type = RemoteSupport.Shared.Clipboard.ClipboardMessageType.TextSync,
            Text = "Hello World",
            SourceId = "session-123"
        };

        var serialized = original.Serialize();
        var deserialized = ClipboardTransport.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(RemoteSupport.Shared.Clipboard.ClipboardMessageType.TextSync, deserialized.Type);
        Assert.Equal("Hello World", deserialized.Text);
        Assert.Equal("session-123", deserialized.SourceId);
    }
}
