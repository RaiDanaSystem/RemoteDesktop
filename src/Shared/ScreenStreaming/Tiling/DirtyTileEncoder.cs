using RemoteSupport.Shared.ScreenStreaming.Encoding;

namespace RemoteSupport.Shared.ScreenStreaming.Tiling;

/// <summary>
/// Splits a BGRA frame into a grid and JPEG-encodes only tiles whose FNV-1a hash changed.
/// </summary>
public sealed class DirtyTileEncoder
{
    private readonly JpegFrameEncoder _jpeg;
    private ulong[]? _hashes;
    private int _width;
    private int _height;
    private int _tileSize;
    private int _cols;
    private int _rows;
    private int _framesSinceKeyframe;

    public DirtyTileEncoder(JpegFrameEncoder jpeg)
    {
        _jpeg = jpeg;
    }

    public (byte[] Packet, int DirtyCount, bool Keyframe, List<byte[]> Packets)? Encode(
        byte[] bgra,
        int width,
        int height,
        uint sequence,
        TileStreamingOptions options)
    {
        if (bgra.Length < width * height * 4)
            return null;

        var tileSize = options.ResolveTileSize(width, height);
        var cols = (width + tileSize - 1) / tileSize;
        var rows = (height + tileSize - 1) / tileSize;
        var gridChanged = _hashes is null
            || _width != width
            || _height != height
            || _tileSize != tileSize;

        if (gridChanged)
        {
            _width = width;
            _height = height;
            _tileSize = tileSize;
            _cols = cols;
            _rows = rows;
            _hashes = new ulong[cols * rows];
            _framesSinceKeyframe = options.KeyframeIntervalFrames;
        }

        var keyframe = gridChanged || _framesSinceKeyframe >= options.KeyframeIntervalFrames;
        if (keyframe)
            _framesSinceKeyframe = 0;
        else
            _framesSinceKeyframe++;

        var dirty = new List<EncodedTile>();
        var hashes = _hashes!;
        var stride = width * 4;

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var x = col * tileSize;
                var y = row * tileSize;
                var tw = Math.Min(tileSize, width - x);
                var th = Math.Min(tileSize, height - y);
                var hash = HashTile(bgra, stride, x, y, tw, th);
                var index = row * cols + col;
                if (!keyframe && hashes[index] == hash)
                    continue;

                hashes[index] = hash;
                var tilePixels = CopyTile(bgra, stride, x, y, tw, th);
                var jpeg = _jpeg.EncodeFrame(tilePixels, tw, th, options.JpegQuality);
                dirty.Add(new EncodedTile((ushort)col, (ushort)row, jpeg));
            }
        }

        if (dirty.Count == 0)
            return null;

        var packets = SplitPackets(width, height, tileSize, cols, rows, sequence, keyframe, dirty);
        return (packets[0], dirty.Count, keyframe, packets);
    }

    private static List<byte[]> SplitPackets(
        int width, int height, int tileSize, int cols, int rows,
        uint sequence, bool keyframe, List<EncodedTile> dirty)
    {
        const int maxTiles = 6;
        const int maxBytes = 20 * 1024;
        var packets = new List<byte[]>();
        var batch = new List<EncodedTile>(maxTiles);
        var batchBytes = 24;

        void Flush()
        {
            if (batch.Count == 0) return;
            packets.Add(TileFramePacket.Serialize(width, height, tileSize, cols, rows, sequence, keyframe, batch));
            batch.Clear();
            batchBytes = 24;
        }

        foreach (var tile in dirty)
        {
            var tileBytes = 8 + tile.Jpeg.Length;
            if (batch.Count >= maxTiles || (batch.Count > 0 && batchBytes + tileBytes > maxBytes))
                Flush();
            batch.Add(tile);
            batchBytes += tileBytes;
        }

        Flush();
        return packets;
    }

    public void Reset()
    {
        _hashes = null;
        _framesSinceKeyframe = 0;
    }

    private static byte[] CopyTile(byte[] bgra, int stride, int x, int y, int w, int h)
    {
        var dest = new byte[w * h * 4];
        var destStride = w * 4;
        for (var row = 0; row < h; row++)
        {
            Buffer.BlockCopy(bgra, (y + row) * stride + x * 4, dest, row * destStride, destStride);
        }
        return dest;
    }

    private static ulong HashTile(byte[] bgra, int stride, int x, int y, int w, int h)
    {
        ulong hash = 14695981039346656037UL;
        for (var row = 0; row < h; row++)
        {
            var offset = (y + row) * stride + x * 4;
            var end = offset + w * 4;
            var i = offset;
            for (; i + 8 <= end; i += 8)
            {
                hash ^= BitConverter.ToUInt64(bgra, i);
                hash *= 1099511628211UL;
            }
            for (; i < end; i++)
            {
                hash ^= bgra[i];
                hash *= 1099511628211UL;
            }
        }

        return hash;
    }
}
