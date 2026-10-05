namespace RemoteSupport.Shared.ScreenStreaming;

public enum FrameFormat : byte
{
    JPEG = 0x01,
    RawBgra = 0x02,
    H264 = 0x03,
    JpegTiles = 0x04
}

public class FrameData
{
    public int Width { get; set; }
    public int Height { get; set; }
    public FrameFormat Format { get; set; } = FrameFormat.JPEG;
    public long TimestampUtcTicks { get; set; }
    public uint SequenceNumber { get; set; }
    public int MonitorIndex { get; set; }
    public int MonitorCount { get; set; }
    public int NativeWidth { get; set; }
    public int NativeHeight { get; set; }
    public int NativeOriginX { get; set; }
    public int NativeOriginY { get; set; }
    public byte[] FrameBytes { get; set; } = [];

    public static byte[] Serialize(FrameData frame)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)frame.Format);
        writer.Write(frame.Width);
        writer.Write(frame.Height);
        writer.Write(frame.TimestampUtcTicks);
        writer.Write(frame.SequenceNumber);
        writer.Write(frame.MonitorIndex);
        writer.Write(frame.MonitorCount);
        writer.Write(frame.NativeWidth);
        writer.Write(frame.NativeHeight);
        writer.Write(frame.NativeOriginX);
        writer.Write(frame.NativeOriginY);
        writer.Write(frame.FrameBytes.Length);
        writer.Write(frame.FrameBytes);

        return ms.ToArray();
    }

    public static FrameData Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        return new FrameData
        {
            Format = (FrameFormat)reader.ReadByte(),
            Width = reader.ReadInt32(),
            Height = reader.ReadInt32(),
            TimestampUtcTicks = reader.ReadInt64(),
            SequenceNumber = reader.ReadUInt32(),
            MonitorIndex = reader.ReadInt32(),
            MonitorCount = reader.ReadInt32(),
            NativeWidth = reader.ReadInt32(),
            NativeHeight = reader.ReadInt32(),
            NativeOriginX = reader.ReadInt32(),
            NativeOriginY = reader.ReadInt32(),
            FrameBytes = reader.ReadBytes(reader.ReadInt32())
        };
    }

    public static bool TryDeserialize(byte[] data, out FrameData? frame)
    {
        try
        {
            frame = Deserialize(data);
            return true;
        }
        catch
        {
            frame = null;
            return false;
        }
    }
}
