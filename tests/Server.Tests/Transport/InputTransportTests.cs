using RemoteSupport.Shared.RemoteInput;
using RemoteSupport.Shared.Transport.Messages;

namespace RemoteSupport.Server.Tests.Transport;

public class InputTransportTests
{
    [Fact]
    public void SerializeDeserialize_MouseMove_PreservesCoordinates()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.MouseMove,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            MouseX = 500,
            MouseY = 300
        };

        var transport = InputTransport.FromInputEvent(evt);
        var serialized = transport.Serialize();
        var deserialized = InputTransport.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(InputEventType.MouseMove, deserialized.Type);
        Assert.Equal(500, deserialized.MouseX);
        Assert.Equal(300, deserialized.MouseY);
    }

    [Fact]
    public void SerializeDeserialize_MouseButton_PreservesButton()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.MouseButton,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 2,
            Button = MouseButton.Left,
            ButtonAction = KeyAction.KeyDown
        };

        var transport = InputTransport.FromInputEvent(evt);
        var serialized = transport.Serialize();
        var deserialized = InputTransport.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(InputEventType.MouseButton, deserialized.Type);
        Assert.Equal(MouseButton.Left, deserialized.Button);
        Assert.Equal(KeyAction.KeyDown, deserialized.Action);
    }

    [Fact]
    public void SerializeDeserialize_RightClick_PreservesButton()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.MouseButton,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 3,
            Button = MouseButton.Right,
            ButtonAction = KeyAction.KeyUp
        };

        var transport = InputTransport.FromInputEvent(evt);
        var serialized = transport.Serialize();
        var deserialized = InputTransport.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(MouseButton.Right, deserialized.Button);
        Assert.Equal(KeyAction.KeyUp, deserialized.Action);
    }

    [Fact]
    public void ToInputEvent_RoundTrip_PreservesData()
    {
        var transport = new InputTransport
        {
            Type = InputEventType.MouseWheel,
            TimestampMs = 1234567890,
            MouseX = 100,
            MouseY = 200,
            WheelDelta = 120
        };

        var evt = transport.ToInputEvent();

        Assert.Equal(InputEventType.MouseWheel, evt.Type);
        Assert.Equal(100, evt.MouseX);
        Assert.Equal(200, evt.MouseY);
        Assert.Equal(120, evt.WheelDelta);
    }
}
