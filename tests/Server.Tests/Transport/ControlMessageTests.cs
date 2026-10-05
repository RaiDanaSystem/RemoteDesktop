using RemoteSupport.Shared.Transport.Messages;

namespace RemoteSupport.Server.Tests.Transport;

public class ControlMessageTests
{
    [Fact]
    public void SerializeDeserialize_Disconnect_PreservesAction()
    {
        var msg = new ControlMessage
        {
            Action = ControlAction.Disconnect,
            Reason = "User disconnected"
        };

        var serialized = msg.Serialize();
        var deserialized = ControlMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(ControlAction.Disconnect, deserialized.Action);
        Assert.Equal("User disconnected", deserialized.Reason);
    }

    [Fact]
    public void SerializeDeserialize_ConsentGranted_PreservesAction()
    {
        var msg = new ControlMessage
        {
            Action = ControlAction.ConsentGranted
        };

        var serialized = msg.Serialize();
        var deserialized = ControlMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(ControlAction.ConsentGranted, deserialized.Action);
    }

    [Fact]
    public void SerializeDeserialize_WithMetadata_PreservesData()
    {
        var msg = new ControlMessage
        {
            Action = ControlAction.ScreenResolutionChanged,
            Metadata = new Dictionary<string, string>
            {
                { "Width", "1920" },
                { "Height", "1080" }
            }
        };

        var serialized = msg.Serialize();
        var deserialized = ControlMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Metadata?.Count);
        Assert.Equal("1920", deserialized.Metadata?["Width"]);
        Assert.Equal("1080", deserialized.Metadata?["Height"]);
    }
}
