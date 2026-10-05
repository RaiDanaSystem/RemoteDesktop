using System.Buffers.Binary;

namespace RemoteSupport.Shared.ScreenStreaming.Tiling;

public readonly record struct EncodedTile(ushort Col, ushort Row, byte[] Jpeg);

/// <summary>
/// Compact binary packet of dirty JPEG tiles. Magic "RDRT".
/// </summary>
public static class TileFramePacket
{
    public static readonly byte[] Magic = "RDRT"u8.ToArray();
    public const byte Version = 1;
    public const byte FlagKeyframe = 1;

    public static bool IsTilePacket(ReadOnlySpan<byte> data)
        => data.Length >= 4
           && data[0] == (byte)'R'
           && data[1] == (byte)'D'
           && data[2] == (byte)'R'
           && data[3] == (byte)'T';

    public static byte[] Serialize(
        int width,
        int height,
        int tileSize,
        int cols,
        int rows,
        uint sequence,
        bool keyframe,
        IReadOnlyList<EncodedTile> tiles)
    {
        var payload = 24;
        foreach (var t in tiles)
            payload += 8 + t.Jpeg.Length;

        var buffer = new byte[payload];
        var span = buffer.AsSpan();
        Magic.CopyTo(span);
        span[4] = Version;
        span[5] = keyframe ? FlagKeyframe : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)tileSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)cols);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], (ushort)rows);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..], sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)tiles.Count);

        var offset = 24;
        foreach (var tile in tiles)
        {
            var slice = span[offset..];
            BinaryPrimitives.WriteUInt16LittleEndian(slice, tile.Col);
            BinaryPrimitives.WriteUInt16LittleEndian(slice[2..], tile.Row);
            BinaryPrimitives.WriteUInt32LittleEndian(slice[4..], (uint)tile.Jpeg.Length);
            tile.Jpeg.CopyTo(slice[8..]);
            offset += 8 + tile.Jpeg.Length;
        }

        return buffer;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out TileFrameHeader header, out List<EncodedTile> tiles)
    {
        header = default;
        tiles = [];
        if (!IsTilePacket(data) || data.Length < 24 || data[4] != Version)
            return false;

        header = new TileFrameHeader(
            Width: BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            Height: BinaryPrimitives.ReadUInt16LittleEndian(data[10..]),
            TileSize: BinaryPrimitives.ReadUInt16LittleEndian(data[12..]),
            Cols: BinaryPrimitives.ReadUInt16LittleEndian(data[14..]),
            Rows: BinaryPrimitives.ReadUInt16LittleEndian(data[16..]),
            Sequence: BinaryPrimitives.ReadUInt32LittleEndian(data[18..]),
            IsKeyframe: (data[5] & FlagKeyframe) != 0,
            TileCount: BinaryPrimitives.ReadUInt16LittleEndian(data[22..]));

        var offset = 24;
        for (var i = 0; i < header.TileCount; i++)
        {
            if (offset + 8 > data.Length) return false;
            var col = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            var row = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
            var len = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            offset += 8;
            if (len > 8 * 1024 * 1024 || offset + len > data.Length) return false;
            var jpeg = data.Slice(offset, (int)len).ToArray();
            tiles.Add(new EncodedTile(col, row, jpeg));
            offset += (int)len;
        }

        return true;
    }
}

public readonly record struct TileFrameHeader(
    int Width,
    int Height,
    int TileSize,
    int Cols,
    int Rows,
    uint Sequence,
    bool IsKeyframe,
    int TileCount);
