using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace RemoteSupport.Shared.ScreenStreaming.Capture;

public class GdiScreenCapture : IScreenCapture
{
    private readonly ILogger? _logger;
    private int _maxWidth;
    private bool _isCapturing;
    private int _currentMonitorIndex;
    private uint _frameSequence;
    public bool IncludeHardwareCursor { get; set; }

    public int MaxWidth
    {
        get => _maxWidth;
        set => _maxWidth = Math.Clamp(value, 640, 3840);
    }
    private int _loggedErrors;

    public GdiScreenCapture(ILogger? logger = null, int maxWidth = 1280)
    {
        _logger = logger;
        _maxWidth = Math.Clamp(maxWidth, 320, 3840);
    }

    public bool IsCapturing => _isCapturing;
    public int CurrentMonitorIndex => _currentMonitorIndex;
    public int MonitorCount => GetMonitorCount();

    public Task<ScreenCaptureResult> StartCaptureAsync(int monitorIndex = 0, CancellationToken cancellationToken = default)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0)
        {
            return Task.FromResult(new ScreenCaptureResult
            {
                IsSuccess = false,
                ErrorMessage = "No monitors found."
            });
        }

        if (monitorIndex < 0 || monitorIndex >= monitors.Count)
            monitorIndex = 0;

        _currentMonitorIndex = monitorIndex;
        _isCapturing = true;
        _frameSequence = 0;

        var monitor = monitors[monitorIndex];
        return Task.FromResult(new ScreenCaptureResult
        {
            IsSuccess = true,
            Width = monitor.Width,
            Height = monitor.Height,
            MonitorIndex = monitorIndex
        });
    }

    public Task<FrameData?> CaptureFrameAsync(CancellationToken cancellationToken = default)
    {
        if (!_isCapturing)
            return Task.FromResult<FrameData?>(null);

        try
        {
            var monitors = GetMonitors();
            if (monitors.Count == 0 || _currentMonitorIndex >= monitors.Count)
                return Task.FromResult<FrameData?>(null);

            var monitor = monitors[_currentMonitorIndex];

            // Fast path when downscaling: let GDI scale straight from the screen (HALFTONE) instead of
            // grabbing a full-size bitmap and resampling it with GDI+ bicubic (very slow at 4K).
            if (!IncludeHardwareCursor && monitor.Width > _maxWidth)
            {
                var fastScale = _maxWidth / (double)monitor.Width;
                var fw = Math.Max(16, (int)(monitor.Width * fastScale) & ~15);
                var fh = Math.Max(16, (int)(monitor.Height * fastScale) & ~15);
                using var fast = CaptureScaledViaStretch(monitor, fw, fh);
                if (fast is not null)
                {
                    var fastPixels = CopyBgra(fast);
                    ForceOpaque(fastPixels);
                    _frameSequence++;
                    return Task.FromResult<FrameData?>(new FrameData
                    {
                        Width = fw,
                        Height = fh,
                        Format = FrameFormat.RawBgra,
                        TimestampUtcTicks = DateTime.UtcNow.Ticks,
                        SequenceNumber = _frameSequence,
                        MonitorIndex = _currentMonitorIndex,
                        MonitorCount = monitors.Count,
                        NativeWidth = monitor.Width,
                        NativeHeight = monitor.Height,
                        NativeOriginX = monitor.BoundsX,
                        NativeOriginY = monitor.BoundsY,
                        FrameBytes = fastPixels
                    });
                }
            }

            using var source = CaptureBitmap(monitor);
            if (source is null)
                return Task.FromResult<FrameData?>(null);

            var scale = Math.Min(1.0, _maxWidth / (double)source.Width);
            var outW = Math.Max(16, (int)(source.Width * scale) & ~15);
            var outH = Math.Max(16, (int)(source.Height * scale) & ~15);

            using var scaled = outW == source.Width && outH == source.Height
                ? null
                : new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
            if (scaled is not null)
            {
                using var g = Graphics.FromImage(scaled);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, 0, 0, outW, outH);
            }

            var encodeTarget = scaled ?? source;
            var pixels = CopyBgra(encodeTarget);
            _frameSequence++;
            if (_frameSequence <= 3)
                _logger?.LogInformation("Captured BGRA frame {Seq} {W}x{H} {Bytes} bytes", _frameSequence, outW, outH, pixels.Length);

            return Task.FromResult<FrameData?>(new FrameData
            {
                Width = outW,
                Height = outH,
                Format = FrameFormat.RawBgra,
                TimestampUtcTicks = DateTime.UtcNow.Ticks,
                SequenceNumber = _frameSequence,
                MonitorIndex = _currentMonitorIndex,
                MonitorCount = monitors.Count,
                NativeWidth = source.Width,
                NativeHeight = source.Height,
                NativeOriginX = monitor.BoundsX,
                NativeOriginY = monitor.BoundsY,
                FrameBytes = pixels
            });
        }
        catch (Exception ex)
        {
            if (_loggedErrors < 5)
            {
                _loggedErrors++;
                _logger?.LogWarning(ex, "Screen capture failed");
            }
            return Task.FromResult<FrameData?>(null);
        }
    }

    public Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        _isCapturing = false;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MonitorInfo>>(GetMonitors());

    public Task SetMonitorAsync(int monitorIndex, CancellationToken cancellationToken = default)
    {
        var monitors = GetMonitors();
        if (monitorIndex >= 0 && monitorIndex < monitors.Count)
            _currentMonitorIndex = monitorIndex;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopCaptureAsync();

    private static byte[] CopyBgra(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = bitmap.Width * 4;
            var pixels = new byte[stride * bitmap.Height];
            if (data.Stride == stride)
            {
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            else
            {
                for (var y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
            }
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private Bitmap? CaptureBitmap(MonitorInfo monitor)
    {
        var bmp = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        try
        {
            g.CopyFromScreen(monitor.BoundsX, monitor.BoundsY, 0, 0,
                new Size(monitor.Width, monitor.Height), CopyPixelOperation.SourceCopy);
            if (IncludeHardwareCursor)
                DrawCursor(g, monitor);
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            return CaptureViaBitBlt(monitor);
        }
    }

    /// <summary>Captures the monitor already scaled to outW x outH using GDI StretchBlt (HALFTONE).</summary>
    private static Bitmap? CaptureScaledViaStretch(MonitorInfo monitor, int outW, int outH)
    {
        var hdcSrc = GetDC(IntPtr.Zero);
        if (hdcSrc == IntPtr.Zero) return null;
        Bitmap? bmp = null;
        try
        {
            bmp = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            var hdcDst = g.GetHdc();
            bool ok;
            try
            {
                SetStretchBltMode(hdcDst, 4 /* HALFTONE */);
                SetBrushOrgEx(hdcDst, 0, 0, IntPtr.Zero);
                ok = StretchBlt(hdcDst, 0, 0, outW, outH, hdcSrc,
                    monitor.BoundsX, monitor.BoundsY, monitor.Width, monitor.Height,
                    0x40CC0020 /* SRCCOPY | CAPTUREBLT */);
            }
            finally
            {
                g.ReleaseHdc(hdcDst);
            }

            if (!ok)
            {
                bmp.Dispose();
                return null;
            }
            return bmp;
        }
        catch
        {
            bmp?.Dispose();
            return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdcSrc);
        }
    }

    /// <summary>GDI leaves alpha at 0 when blitting into a 32-bit bitmap; downstream encoders expect opaque pixels.</summary>
    private static void ForceOpaque(byte[] bgra)
    {
        var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bgra.AsSpan());
        for (var i = 0; i < px.Length; i++)
            px[i] |= 0xFF000000u;
    }

    private Bitmap? CaptureViaBitBlt(MonitorInfo monitor)
    {
        var hdcSrc = GetDC(IntPtr.Zero);
        if (hdcSrc == IntPtr.Zero) return null;
        var bmp = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        var hdcDst = g.GetHdc();
        try
        {
            BitBlt(hdcDst, 0, 0, monitor.Width, monitor.Height, hdcSrc, monitor.BoundsX, monitor.BoundsY, 0x00CC0020);
            g.ReleaseHdc(hdcDst);
            hdcDst = IntPtr.Zero;
            if (IncludeHardwareCursor)
                DrawCursor(g, monitor);
            return bmp;
        }
        finally
        {
            if (hdcDst != IntPtr.Zero)
                g.ReleaseHdc(hdcDst);
            ReleaseDC(IntPtr.Zero, hdcSrc);
        }
    }

    private static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            monitors.Add(new MonitorInfo
            {
                Index = i,
                Name = screens[i].DeviceName,
                Width = Math.Max(1, screens[i].Bounds.Width),
                Height = Math.Max(1, screens[i].Bounds.Height),
                IsPrimary = screens[i].Primary,
                BoundsX = screens[i].Bounds.X,
                BoundsY = screens[i].Bounds.Y
            });
        }

        if (monitors.Count == 0)
        {
            monitors.Add(new MonitorInfo
            {
                Index = 0,
                Name = "Primary",
                Width = Math.Max(1, GetSystemMetrics(0)),
                Height = Math.Max(1, GetSystemMetrics(1)),
                IsPrimary = true
            });
        }

        return monitors;
    }

    private static int GetMonitorCount() => Math.Max(1, Screen.AllScreens.Length);

    private static void DrawCursor(Graphics g, MonitorInfo monitor)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CursorShowing) == 0 || info.hCursor == IntPtr.Zero)
            return;

        if (!GetIconInfo(info.hCursor, out var iconInfo))
            return;

        try
        {
            var x = info.ptScreenPos.X - monitor.BoundsX - iconInfo.xHotspot;
            var y = info.ptScreenPos.Y - monitor.BoundsY - iconInfo.yHotspot;
            var hdc = g.GetHdc();
            try
            {
                DrawIcon(hdc, x, y, info.hCursor);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        finally
        {
            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
        }
    }

    private const int CursorShowing = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool DrawIcon(IntPtr hDC, int x, int y, IntPtr hIcon);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr lppt);
    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);
}
