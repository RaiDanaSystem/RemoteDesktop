using System.Text.Json;
using RemoteSupport.Shared.Transport;

namespace RemoteSupport.Server.Tests.Transport;

public class TransportEnvelopeTests
{
    [Fact]
    public void Serialize_Deserialize_RoundTrip_Ping()
    {
        var ping = new PingMessage { PingId = "test-123", TimestampMs = 1234567890L };
        var payload = JsonSerializer.SerializeToUtf8Bytes(ping);

        var envelope = new TransportEnvelope
        {
            MessageType = TransportMessageType.Ping,
            Payload = payload
        };

        var bytes = envelope.Serialize();
        var deserialized = TransportEnvelope.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(TransportMessageType.Ping, deserialized.MessageType);
        Assert.Equal(1, deserialized.Version);
        Assert.Equal(envelope.MessageId, deserialized.MessageId);
        Assert.True(payload.SequenceEqual(deserialized.Payload.ToArray()));
    }

    [Fact]
    public void Serialize_Deserialize_RoundTrip_Pong()
    {
        var pong = new PongMessage { PingId = "test-123", TimestampMs = 1234567890L };
        var payload = JsonSerializer.SerializeToUtf8Bytes(pong);

        var envelope = new TransportEnvelope
        {
            MessageType = TransportMessageType.Pong,
            Payload = payload
        };

        var bytes = envelope.Serialize();
        var deserialized = TransportEnvelope.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(TransportMessageType.Pong, deserialized.MessageType);
        Assert.True(payload.SequenceEqual(deserialized.Payload.ToArray()));
    }

    [Fact]
    public void Serialize_Deserialize_EmptyPayload()
    {
        var envelope = new TransportEnvelope
        {
            MessageType = TransportMessageType.Control,
            Payload = ReadOnlyMemory<byte>.Empty
        };

        var bytes = envelope.Serialize();
        var deserialized = TransportEnvelope.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(TransportMessageType.Control, deserialized.MessageType);
        Assert.Equal(0, deserialized.Payload.Length);
    }

    [Fact]
    public void Deserialize_TooShort_ReturnsNull()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var result = TransportEnvelope.Deserialize(bytes);
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_InvalidHeaderLength_ReturnsNull()
    {
        var bytes = new byte[100];
        BitConverter.GetBytes(999999).CopyTo(bytes, 0);
        var result = TransportEnvelope.Deserialize(bytes);
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_TruncatedData_ReturnsNull()
    {
        var envelope = new TransportEnvelope
        {
            MessageType = TransportMessageType.Ping,
            Payload = new byte[100]
        };
        var bytes = envelope.Serialize();
        var truncated = bytes.Take(10).ToArray();
        var result = TransportEnvelope.Deserialize(truncated);
        Assert.Null(result);
    }

    [Fact]
    public void Serialize_ProducesValidBinaryFormat()
    {
        var envelope = new TransportEnvelope
        {
            MessageType = TransportMessageType.ScreenFrame,
            Payload = new byte[] { 0xAA, 0xBB, 0xCC }
        };

        var bytes = envelope.Serialize();

        Assert.True(bytes.Length > 4);
        var headerLength = BitConverter.ToInt32(bytes);
        Assert.True(headerLength > 0);
        Assert.True(headerLength < bytes.Length);
    }

    [Fact]
    public void TransportEnvelope_MessageTypes_AllDefined()
    {
        var values = Enum.GetValues<TransportMessageType>();
        Assert.True(values.Length >= 12);
        Assert.Contains(TransportMessageType.Ping, values);
        Assert.Contains(TransportMessageType.Pong, values);
        Assert.Contains(TransportMessageType.ScreenFrame, values);
        Assert.Contains(TransportMessageType.Input, values);
        Assert.Contains(TransportMessageType.Audio, values);
        Assert.Contains(TransportMessageType.FileOffer, values);
        Assert.Contains(TransportMessageType.FileChunk, values);
        Assert.Contains(TransportMessageType.FileAck, values);
        Assert.Contains(TransportMessageType.Clipboard, values);
        Assert.Contains(TransportMessageType.Chat, values);
        Assert.Contains(TransportMessageType.Control, values);
        Assert.Contains(TransportMessageType.Error, values);
    }

    [Fact]
    public void MessageId_IsUniquePerEnvelope()
    {
        var env1 = new TransportEnvelope { MessageType = TransportMessageType.Ping };
        var env2 = new TransportEnvelope { MessageType = TransportMessageType.Ping };
        Assert.NotEqual(env1.MessageId, env2.MessageId);
    }
}
