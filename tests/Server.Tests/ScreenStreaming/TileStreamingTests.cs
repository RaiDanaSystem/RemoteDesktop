using RemoteSupport.Shared.ScreenStreaming;
using RemoteSupport.Shared.ScreenStreaming.Encoding;
using RemoteSupport.Shared.ScreenStreaming.Tiling;

namespace RemoteSupport.Server.Tests.ScreenStreaming;

public class TileStreamingTests
{
    [Fact]
    public void ChooseTileSize_ScalesWithResolution()
    {
        Assert.Equal(48, TileStreamingOptions.ChooseTileSize(640, 360));
        Assert.Equal(64, TileStreamingOptions.ChooseTileSize(1280, 720));
        Assert.Equal(96, TileStreamingOptions.ChooseTileSize(1920, 1080));
        Assert.Equal(128, TileStreamingOptions.ChooseTileSize(2560, 1440));
    }

    [Fact]
    public void TilePacket_Roundtrip()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0x01, 0x02 };
        var tiles = new List<EncodedTile> { new(1, 2, jpeg) };
        var bytes = TileFramePacket.Serialize(1280, 720, 64, 20, 12, 9, true, tiles);

        Assert.True(TileFramePacket.TryDeserialize(bytes, out var header, out var parsed));
        Assert.Equal(1280, header.Width);
        Assert.Equal(720, header.Height);
        Assert.Equal(64, header.TileSize);
        Assert.True(header.IsKeyframe);
        Assert.Equal(9u, header.Sequence);
        Assert.Single(parsed);
        Assert.Equal((ushort)1, parsed[0].Col);
        Assert.Equal((ushort)2, parsed[0].Row);
        Assert.Equal(jpeg, parsed[0].Jpeg);
    }

    [Fact]
    public void DirtyTileEncoder_UnchangedFrame_SendsNothing()
    {
        var encoder = new DirtyTileEncoder(new JpegFrameEncoder(40));
        var options = new TileStreamingOptions { TileSize = 32, JpegQuality = 40, KeyframeIntervalFrames = 1000 };
        var pixels = SolidBgra(64, 64, 30, 80, 120);

        var first = encoder.Encode(pixels, 64, 64, 1, options);
        Assert.NotNull(first);
        Assert.True(first.Value.Keyframe);

        var second = encoder.Encode(pixels, 64, 64, 2, options);
        Assert.Null(second);
    }

    [Fact]
    public void DirtyTileEncoder_ChangedTile_SendsOnlyDirty()
    {
        var encoder = new DirtyTileEncoder(new JpegFrameEncoder(40));
        var options = new TileStreamingOptions { TileSize = 32, JpegQuality = 40, KeyframeIntervalFrames = 1000 };
        var pixels = SolidBgra(64, 64, 10, 10, 10);

        encoder.Encode(pixels, 64, 64, 1, options);

        PaintRect(pixels, 64, 0, 0, 32, 32, 200, 10, 10);
        var delta = encoder.Encode(pixels, 64, 64, 2, options);
        Assert.NotNull(delta);
        Assert.False(delta.Value.Keyframe);
        Assert.True(TileFramePacket.TryDeserialize(delta.Value.Packet, out _, out var tiles));
        Assert.Single(tiles);
        Assert.Equal((ushort)0, tiles[0].Col);
        Assert.Equal((ushort)0, tiles[0].Row);
    }

    [Fact]
    public void DirtyTileEncoder_Keyframe_SplitsIntoSmallPackets()
    {
        var encoder = new DirtyTileEncoder(new JpegFrameEncoder(40));
        var options = new TileStreamingOptions { TileSize = 32, JpegQuality = 40, KeyframeIntervalFrames = 1000 };
        var pixels = SolidBgra(256, 256, 40, 80, 120);

        var first = encoder.Encode(pixels, 256, 256, 1, options);
        Assert.NotNull(first);
        Assert.True(first.Value.Keyframe);
        Assert.True(first.Value.Packets.Count > 1);
        Assert.All(first.Value.Packets, p => Assert.True(p.Length < 64 * 1024));
    }

    private static byte[] SolidBgra(int width, int height, byte b, byte g, byte r)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static void PaintRect(byte[] pixels, int width, int x, int y, int w, int h, byte b, byte g, byte r)
    {
        for (var row = y; row < y + h; row++)
        {
            for (var col = x; col < x + w; col++)
            {
                var i = (row * width + col) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        }
    }
}
