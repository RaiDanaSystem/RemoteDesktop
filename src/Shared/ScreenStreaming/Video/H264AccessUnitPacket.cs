using System.Buffers.Binary;

namespace RemoteSupport.Shared.ScreenStreaming.Video;

public readonly record struct H264PacketHeader(
    int Width,
    int Height,
    uint Sequence,
    bool Keyframe,
    ushort PartIndex,
    ushort PartCount,
    int PayloadOffset,
    int PayloadLength);

/// <summary>
/// Fragmented Annex-B H.264 access unit. Magic "RDH2".
/// </summary>
public static class H264AccessUnitPacket
{
    public static readonly byte[] Magic = "RDH2"u8.ToArray();
    public const byte Version = 1;
    public const byte FlagKeyframe = 1;
    public const int HeaderSize = 24;
    public const int MaxPartBytes = 12_000;

    public static bool IsPacket(ReadOnlySpan<byte> data)
        => data.Length >= HeaderSize
           && data[0] == (byte)'R'
           && data[1] == (byte)'D'
           && data[2] == (byte)'H'
           && data[3] == (byte)'2'
           && data[4] == Version;

    public static List<byte[]> Split(int width, int height, uint sequence, bool keyframe, ReadOnlySpan<byte> annexB)
    {
        var packets = new List<byte[]>();
        var partCount = Math.Max(1, (annexB.Length + MaxPartBytes - 1) / MaxPartBytes);
        for (var i = 0; i < partCount; i++)
        {
            var start = i * MaxPartBytes;
            var len = Math.Min(MaxPartBytes, annexB.Length - start);
            packets.Add(Serialize(width, height, sequence, keyframe, (ushort)i, (ushort)partCount, annexB.Slice(start, len)));
        }
        return packets;
    }

    public static byte[] Serialize(
        int width, int height, uint sequence, bool keyframe,
        ushort partIndex, ushort partCount, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[HeaderSize + payload.Length];
        var span = buffer.AsSpan();
        Magic.CopyTo(span);
        span[4] = Version;
        span[5] = keyframe ? FlagKeyframe : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)height);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], partIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], partCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)payload.Length);
        payload.CopyTo(span[HeaderSize..]);
        return buffer;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out H264PacketHeader header)
    {
        header = default;
        if (!IsPacket(data))
            return false;

        var payloadLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        if (payloadLen < 0 || HeaderSize + payloadLen > data.Length)
            return false;

        header = new H264PacketHeader(
            Width: BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            Height: BinaryPrimitives.ReadUInt16LittleEndian(data[10..]),
            Sequence: BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            Keyframe: (data[5] & FlagKeyframe) != 0,
            PartIndex: BinaryPrimitives.ReadUInt16LittleEndian(data[16..]),
            PartCount: BinaryPrimitives.ReadUInt16LittleEndian(data[18..]),
            PayloadOffset: HeaderSize,
            PayloadLength: payloadLen);
        return true;
    }
}

public sealed class H264FrameAssembler
{
    private uint _sequence;
    private byte[]?[] _parts = [];
    private int _received;
    private int _width;
    private int _height;
    private bool _keyframe;

    public bool TryAdd(ReadOnlySpan<byte> packet, out byte[] annexB, out int width, out int height, out bool keyframe)
    {
        annexB = [];
        width = 0;
        height = 0;
        keyframe = false;
        if (!H264AccessUnitPacket.TryParse(packet, out var header))
            return false;

        if (header.Sequence != _sequence || header.PartCount != _parts.Length)
        {
            _sequence = header.Sequence;
            _parts = new byte[]?[header.PartCount];
            _received = 0;
            _width = header.Width;
            _height = header.Height;
            _keyframe = header.Keyframe;
        }

        if (header.PartIndex >= _parts.Length)
            return false;
        if (_parts[header.PartIndex] is null)
        {
            _parts[header.PartIndex] = packet.Slice(header.PayloadOffset, header.PayloadLength).ToArray();
            _received++;
        }

        if (_received < _parts.Length)
            return false;

        var total = 0;
        foreach (var part in _parts)
            total += part!.Length;
        annexB = new byte[total];
        var offset = 0;
        foreach (var part in _parts)
        {
            Buffer.BlockCopy(part!, 0, annexB, offset, part!.Length);
            offset += part.Length;
        }

        width = _width;
        height = _height;
        keyframe = _keyframe;
        _parts = [];
        _received = 0;
        return true;
    }
}
