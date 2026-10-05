using RemoteSupport.Shared.ScreenStreaming;
using RemoteSupport.Shared.Transport.Messages;

namespace RemoteSupport.Server.Tests.Transport;

public class ScreenFrameTransportTests
{
    [Fact]
    public void SerializeDeserialize_RoundTrip_PreservesData()
    {
        var frame = new FrameData
        {
            Width = 1920,
            Height = 1080,
            Format = FrameFormat.JPEG,
            TimestampUtcTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 42,
            MonitorIndex = 0,
            MonitorCount = 2,
            FrameBytes = new byte[] { 1, 2, 3, 4, 5 }
        };

        var transport = ScreenFrameTransport.FromFrameData(frame);
        var serialized = transport.Serialize();
        var deserialized = ScreenFrameTransport.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(42u, deserialized.FrameId);
        Assert.Equal(1920, deserialized.Width);
        Assert.Equal(1080, deserialized.Height);
        Assert.Equal(FrameFormat.JPEG, deserialized.Format);
        Assert.Equal(0, deserialized.MonitorIndex);
        Assert.Equal(2, deserialized.MonitorCount);
        Assert.Equal(frame.FrameBytes, deserialized.FramePayload.ToArray());
    }

    [Fact]
    public void TryDeserialize_InvalidData_ReturnsFalse()
    {
        var result = ScreenFrameTransport.TryDeserialize(new byte[] { 0, 1, 2 }, out var frame);

        Assert.False(result);
        Assert.Null(frame);
    }

    [Fact]
    public void ToFrameData_RoundTrip_PreservesMetadata()
    {
        var transport = new ScreenFrameTransport
        {
            FrameId = 100,
            TimestampMs = 1234567890,
            Width = 1280,
            Height = 720,
            Format = FrameFormat.JPEG,
            MonitorIndex = 1,
            MonitorCount = 3,
            FramePayload = new byte[] { 10, 20, 30 }
        };

        var frameData = transport.ToFrameData();

        Assert.Equal(1280, frameData.Width);
        Assert.Equal(720, frameData.Height);
        Assert.Equal(100u, frameData.SequenceNumber);
        Assert.Equal(1, frameData.MonitorIndex);
        Assert.Equal(3, frameData.MonitorCount);
        Assert.Equal(new byte[] { 10, 20, 30 }, frameData.FrameBytes);
    }
}
