using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteSupport.Shared.ScreenStreaming;

namespace RemoteSupport.Shared.Transport.Messages;

/// <summary>
/// Transport message wrapping a screen frame for sending over the DataChannel.
/// Contains both metadata and the encoded frame payload.
/// </summary>
public sealed class ScreenFrameTransport
{
    public uint FrameId { get; init; }
    public long TimestampMs { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public FrameFormat Format { get; init; } = FrameFormat.JPEG;
    public int MonitorIndex { get; init; }
    public int MonitorCount { get; init; }
    public int NativeWidth { get; init; }
    public int NativeHeight { get; init; }

    /// <summary>
    /// The encoded frame bytes (JPEG, etc.). Sent as raw payload after the JSON header.
    /// </summary>
    [JsonIgnore]
    public ReadOnlyMemory<byte> FramePayload { get; init; }

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public byte[] Serialize()
    {
        var header = new ScreenFrameHeader
        {
            FrameId = FrameId,
            TimestampMs = TimestampMs,
            Width = Width,
            Height = Height,
            Format = Format,
            MonitorIndex = MonitorIndex,
            MonitorCount = MonitorCount,
            NativeWidth = NativeWidth,
            NativeHeight = NativeHeight,
            PayloadLength = FramePayload.Length
        };

        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, s_options);
        var headerLen = BitConverter.GetBytes(headerBytes.Length);

        var result = new byte[4 + headerBytes.Length + FramePayload.Length];
        Buffer.BlockCopy(headerLen, 0, result, 0, 4);
        Buffer.BlockCopy(headerBytes, 0, result, 4, headerBytes.Length);
        if (FramePayload.Length > 0)
            Buffer.BlockCopy(FramePayload.ToArray(), 0, result, 4 + headerBytes.Length, FramePayload.Length);

        return result;
    }

    public static ScreenFrameTransport? Deserialize(byte[] data)
    {
        if (data.Length < 4) return null;

        var headerLen = BitConverter.ToInt32(data, 0);
        if (headerLen <= 0 || headerLen > 64 * 1024) return null;
        if (data.Length < 4 + headerLen) return null;

        var headerBytes = data.AsSpan(4, headerLen).ToArray();
        var header = JsonSerializer.Deserialize<ScreenFrameHeader>(headerBytes, s_options);
        if (header is null) return null;

        var payloadStart = 4 + headerLen;
        var payload = data.Length > payloadStart
            ? data.AsMemory(payloadStart)
            : ReadOnlyMemory<byte>.Empty;

        return new ScreenFrameTransport
        {
            FrameId = header.FrameId,
            TimestampMs = header.TimestampMs,
            Width = header.Width,
            Height = header.Height,
            Format = header.Format,
            MonitorIndex = header.MonitorIndex,
            MonitorCount = header.MonitorCount,
            NativeWidth = header.NativeWidth,
            NativeHeight = header.NativeHeight,
            FramePayload = payload
        };
    }

    public static bool TryDeserialize(byte[] data, out ScreenFrameTransport? frame)
    {
        frame = Deserialize(data);
        return frame is not null;
    }

    public static ScreenFrameTransport FromFrameData(FrameData frame)
    {
        return new ScreenFrameTransport
        {
            FrameId = frame.SequenceNumber,
            TimestampMs = new DateTimeOffset(frame.TimestampUtcTicks, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            Width = frame.Width,
            Height = frame.Height,
            Format = frame.Format,
            MonitorIndex = frame.MonitorIndex,
            MonitorCount = frame.MonitorCount,
            NativeWidth = frame.NativeWidth,
            NativeHeight = frame.NativeHeight,
            FramePayload = frame.FrameBytes
        };
    }

    public FrameData ToFrameData()
    {
        return new FrameData
        {
            Width = Width,
            Height = Height,
            Format = Format,
            TimestampUtcTicks = new DateTimeOffset(TimestampMs, TimeSpan.Zero).Ticks,
            SequenceNumber = FrameId,
            MonitorIndex = MonitorIndex,
            MonitorCount = MonitorCount,
            NativeWidth = NativeWidth,
            NativeHeight = NativeHeight,
            FrameBytes = FramePayload.ToArray()
        };
    }

    private sealed class ScreenFrameHeader
    {
        public uint FrameId { get; set; }
        public long TimestampMs { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public FrameFormat Format { get; set; }
        public int MonitorIndex { get; set; }
        public int MonitorCount { get; set; }
        public int NativeWidth { get; set; }
        public int NativeHeight { get; set; }
        public int PayloadLength { get; set; }
    }
}
