using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteSupport.Shared.Diagnostics;
using RemoteSupport.Shared.ScreenStreaming;
using RemoteSupport.Shared.ScreenStreaming.Encoding;
using RemoteSupport.Shared.ScreenStreaming.Tiling;
using RemoteSupport.Shared.ScreenStreaming.Video;

namespace SupportAgent.Services.Screen;

public class WpfFrameRenderer : IFrameRenderer
{
    private readonly Action<BitmapSource> _onFrameRendered;
    private readonly IFrameEncoder _encoder;
    private WriteableBitmap? _canvas;
    private int _canvasWidth;
    private int _canvasHeight;
    private readonly object _tileLock = new();
    private readonly List<(int X, int Y, int W, int H, byte[] Pixels)> _pendingTiles = new();
    private int _pendingWidth;
    private int _pendingHeight;
    private bool _flushScheduled;
    private int _flushedBatches;
    private readonly H264FrameAssembler _h264Assembler = new();
    private readonly OpenH264Decoder _h264Decoder = new();

    public WpfFrameRenderer(Action<BitmapSource> onFrameRendered, IFrameEncoder encoder)
    {
        _onFrameRendered = onFrameRendered;
        _encoder = encoder;
    }

    public Task RenderFrameAsync(byte[] frameData, CancellationToken cancellationToken = default)
    {
        if (frameData.Length == 0) return Task.CompletedTask;

        if (H264AccessUnitPacket.IsPacket(frameData))
            return RenderH264Async(frameData);

        if (TileFramePacket.TryDeserialize(frameData, out var header, out var tiles))
            return RenderTilesAsync(header, tiles);

        byte[] jpegBytes = frameData;
        if (!(frameData.Length >= 2 && frameData[0] == 0xFF && frameData[1] == 0xD8)
            && FrameData.TryDeserialize(frameData, out var wrapped)
            && wrapped is not null
            && wrapped.FrameBytes.Length > 0)
        {
            if (TileFramePacket.TryDeserialize(wrapped.FrameBytes, out header, out tiles))
                return RenderTilesAsync(header, tiles);
            jpegBytes = wrapped.FrameBytes;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return Task.CompletedTask;

        dispatcher.BeginInvoke(() =>
        {
            try
            {
                var image = new BitmapImage();
                using var ms = new MemoryStream(jpegBytes);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms;
                image.EndInit();
                image.Freeze();
                _onFrameRendered(image);
            }
            catch
            {
                try
                {
                    var pixels = _encoder.DecodeFrame(jpegBytes, out var width, out var height);
                    var bitmap = BitmapSource.Create(
                        width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
                    bitmap.Freeze();
                    _onFrameRendered(bitmap);
                }
                catch { }
            }
        }, DispatcherPriority.Render);

        return Task.CompletedTask;
    }

    private Task RenderH264Async(byte[] packet)
    {
        if (!_h264Assembler.TryAdd(packet, out var annexB, out var width, out var height, out _))
            return Task.CompletedTask;

        byte[]? bgra;
        try
        {
            bgra = _h264Decoder.DecodeAnnexB(annexB, width, height);
        }
        catch
        {
            return Task.CompletedTask;
        }

        if (bgra is null || bgra.Length == 0)
            return Task.CompletedTask;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return Task.CompletedTask;

        dispatcher.BeginInvoke(() =>
        {
            try
            {
                EnsureCanvas(width, height);
                if (_canvas is null) return;
                _canvas.Lock();
                try
                {
                    _canvas.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
                }
                finally
                {
                    _canvas.Unlock();
                }
                _onFrameRendered(_canvas);
            }
            catch { }
        }, DispatcherPriority.Render);

        return Task.CompletedTask;
    }

    private Task RenderTilesAsync(TileFrameHeader header, List<EncodedTile> tiles)
    {
        var decoded = new List<(int X, int Y, int W, int H, byte[] Pixels)>(tiles.Count);
        foreach (var tile in tiles)
        {
            try
            {
                var pixels = _encoder.DecodeFrame(tile.Jpeg, out var tw, out var th);
                decoded.Add((tile.Col * header.TileSize, tile.Row * header.TileSize, tw, th, pixels));
            }
            catch { }
        }

        if (decoded.Count == 0)
            return Task.CompletedTask;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return Task.CompletedTask;

        lock (_tileLock)
        {
            _pendingWidth = header.Width;
            _pendingHeight = header.Height;
            _pendingTiles.AddRange(decoded);
            if (_flushScheduled)
                return Task.CompletedTask;
            _flushScheduled = true;
        }

        dispatcher.BeginInvoke(() =>
        {
            List<(int X, int Y, int W, int H, byte[] Pixels)> batch;
            int width, height;
            lock (_tileLock)
            {
                batch = [.. _pendingTiles];
                _pendingTiles.Clear();
                width = _pendingWidth;
                height = _pendingHeight;
                _flushScheduled = false;
            }

            try
            {
                EnsureCanvas(width, height);
                if (_canvas is null) return;

                _canvas.Lock();
                try
                {
                    foreach (var tile in batch)
                    {
                        var w = Math.Min(tile.W, width - tile.X);
                        var h = Math.Min(tile.H, height - tile.Y);
                        if (w <= 0 || h <= 0) continue;
                        try
                        {
                            var rect = new Int32Rect(tile.X, tile.Y, w, h);
                            _canvas.WritePixels(rect, tile.Pixels, tile.W * 4, 0);
                        }
                        catch { }
                    }
                }
                finally
                {
                    _canvas.Unlock();
                }

                _onFrameRendered(_canvas);
                _flushedBatches++;
                if (_flushedBatches <= 5 || _flushedBatches % 30 == 0)
                {
                    SessionTrace.Write("support-render",
                        $"flush tiles={batch.Count} canvas={width}x{height} batch#{_flushedBatches}");
                }
            }
            catch (Exception ex)
            {
                SessionTrace.Write("support-render", $"flush failed: {ex.Message}");
            }
        }, DispatcherPriority.Render);

        return Task.CompletedTask;
    }

    private void EnsureCanvas(int width, int height)
    {
        if (_canvas is not null && _canvasWidth == width && _canvasHeight == height)
            return;

        _canvas = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _canvasWidth = width;
        _canvasHeight = height;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _canvas = null;
            _onFrameRendered(null!);
        });
        return Task.CompletedTask;
    }
}
