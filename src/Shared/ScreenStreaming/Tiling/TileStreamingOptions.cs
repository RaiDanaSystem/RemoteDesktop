namespace RemoteSupport.Shared.ScreenStreaming.Tiling;

/// <summary>
/// Grid size and JPEG settings for dirty-tile screen streaming.
/// TileSize = 0 picks an automatic size from output resolution.
/// </summary>
public sealed class TileStreamingOptions
{
    public int MaxWidth { get; set; } = 1280;
    public int JpegQuality { get; set; } = 45;
    public int TileSize { get; set; }
    public int KeyframeIntervalFrames { get; set; } = 30;

    public static int ChooseTileSize(int width, int height)
    {
        var pixels = Math.Max(1, width) * (long)Math.Max(1, height);
        if (pixels <= 640 * 360) return 48;
        if (pixels <= 1280 * 800) return 64;
        if (pixels <= 1920 * 1080) return 96;
        return 128;
    }

    public int ResolveTileSize(int width, int height)
    {
        if (TileSize is >= 16 and <= 256)
            return TileSize;
        return ChooseTileSize(width, height);
    }
}
