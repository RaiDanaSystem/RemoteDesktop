using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteSupport.Shared.Transport;

/// <summary>
/// Minimal transport envelope for all messages sent over the data channel.
/// Version-aware to allow future protocol evolution.
/// </summary>
public sealed class TransportEnvelope
{
    public int Version { get; set; } = 1;
    public TransportMessageType MessageType { get; set; }
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public ReadOnlyMemory<byte> Payload { get; set; }

    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public byte[] Serialize()
    {
        var header = new EnvelopeHeader
        {
            Version = Version,
            MessageType = MessageType,
            MessageId = MessageId,
            Timestamp = Timestamp,
            PayloadLength = Payload.Length
        };

        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, s_serializerOptions);
        var headerLength = BitConverter.GetBytes(headerBytes.Length);

        var result = new byte[4 + headerBytes.Length + Payload.Length];
        Buffer.BlockCopy(headerLength, 0, result, 0, 4);
        Buffer.BlockCopy(headerBytes, 0, result, 4, headerBytes.Length);
        if (Payload.Length > 0)
            Buffer.BlockCopy(Payload.ToArray(), 0, result, 4 + headerBytes.Length, Payload.Length);

        return result;
    }

    public static TransportEnvelope? Deserialize(ReadOnlyMemory<byte> data)
    {
        if (data.Length < 4) return null;

        var span = data.Span;
        var headerLength = BitConverter.ToInt32(span);
        if (headerLength <= 0 || headerLength > 64 * 1024) return null;

        if (data.Length < 4 + headerLength) return null;

        var headerBytes = span.Slice(4, headerLength).ToArray();
        var header = JsonSerializer.Deserialize<EnvelopeHeader>(headerBytes, s_serializerOptions);
        if (header is null) return null;

        var payloadStart = 4 + headerLength;
        var payloadLength = data.Length - payloadStart;
        var payload = payloadLength > 0
            ? data.Slice(payloadStart, payloadLength)
            : ReadOnlyMemory<byte>.Empty;

        return new TransportEnvelope
        {
            Version = header.Version,
            MessageType = header.MessageType,
            MessageId = header.MessageId,
            Timestamp = header.Timestamp,
            Payload = payload
        };
    }

    public static TransportEnvelope Deserialize(ReadOnlyMemory<byte> data, out int bytesConsumed)
    {
        bytesConsumed = 0;
        if (data.Length < 4) return null;

        var span = data.Span;
        var headerLength = BitConverter.ToInt32(span);
        if (headerLength <= 0 || headerLength > 64 * 1024) return null;

        if (data.Length < 4 + headerLength) return null;

        var headerBytes = span.Slice(4, headerLength).ToArray();
        var header = JsonSerializer.Deserialize<EnvelopeHeader>(headerBytes, s_serializerOptions);
        if (header is null) return null;

        var payloadStart = 4 + headerLength;
        var payloadLength = data.Length - payloadStart;
        var payload = payloadLength > 0
            ? data.Slice(payloadStart, payloadLength)
            : ReadOnlyMemory<byte>.Empty;

        bytesConsumed = payloadStart + header.PayloadLength;
        return new TransportEnvelope
        {
            Version = header.Version,
            MessageType = header.MessageType,
            MessageId = header.MessageId,
            Timestamp = header.Timestamp,
            Payload = payload
        };
    }

    private sealed class EnvelopeHeader
    {
        public int Version { get; set; }
        public TransportMessageType MessageType { get; set; }
        public string MessageId { get; set; } = string.Empty;
        public long Timestamp { get; set; }
        public int PayloadLength { get; set; }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransportMessageType
{
    Ping,
    Pong,
    ScreenFrame,
    Input,
    Audio,
    FileOffer,
    FileChunk,
    FileAck,
    Clipboard,
    Chat,
    Control,
    Error
}
