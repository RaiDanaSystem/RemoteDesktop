using H264Sharp;

namespace RemoteSupport.Shared.ScreenStreaming.Video;

public sealed class OpenH264Encoder : IDisposable
{
    private H264Encoder? _encoder;
    private int _width;
    private int _height;
    private readonly int _fps;
    private readonly int _bitrate;
    private bool _forceKeyframe = true;

    private readonly bool _correctColors;
    private bool _fast;
    private int _fastFailures;

    /// <param name="correctColors">
    /// The captured frames are BGRA. The legacy stream labels them RGBA (red/blue swapped; the Windows viewer
    /// compensates when decoding). Viewers that decode with a hardware codec ask for correct colors instead.
    /// </param>
    /// <param name="fast">
    /// Multi-threaded encoder setup (one slice per thread, bitrate rate control). Much faster at 1440p/4K.
    /// Falls back to the legacy single-config path automatically if the native library rejects it.
    /// </param>
    public OpenH264Encoder(int width, int height, int fps = 15, int bitrate = 1_500_000, bool correctColors = false, bool fast = false)
    {
        _correctColors = correctColors;
        _fast = fast;
        _fps = Math.Clamp(fps, 5, 60);
        _bitrate = Math.Clamp(bitrate, 250_000, 80_000_000);
        Reconfigure(Align16(width), Align16(height));
    }

    public void RequestKeyframe() => _forceKeyframe = true;

    public void EnsureSize(int width, int height)
    {
        width = Align16(width);
        height = Align16(height);
        if (width == _width && height == _height)
            return;
        Reconfigure(width, height);
    }

    public byte[]? EncodeBgra(byte[] bgra, int width, int height, out bool keyframe)
    {
        keyframe = false;
        width = Align16(width);
        height = Align16(height);
        EnsureSize(width, height);
        if (_encoder is null)
            return null;

        if (_forceKeyframe)
        {
            _encoder.ForceIntraFrame();
            _forceKeyframe = false;
            keyframe = true;
        }

        var image = new ImageData(_correctColors ? ImageType.Bgra : ImageType.Rgba, width, height, width * 4, bgra);
        if (!_encoder.Encode(image, out EncodedData[]? nalus) || nalus is null || nalus.Length == 0)
        {
            // The fast configuration never produced output: go back to the proven legacy setup.
            if (_fast && ++_fastFailures >= 3)
            {
                _fast = false;
                Reconfigure(_width, _height);
            }
            return null;
        }
        _fastFailures = 0;

        using var ms = new MemoryStream();
        foreach (var nalu in nalus)
        {
            var bytes = nalu.GetBytes();
            if (bytes is { Length: > 0 })
                ms.Write(bytes, 0, bytes.Length);
        }

        var annexB = ms.ToArray();
        if (IsKeyframeNal(annexB))
            keyframe = true;
        return annexB.Length == 0 ? null : annexB;
    }

    private void Reconfigure(int width, int height)
    {
        _encoder?.Dispose();
        _width = width;
        _height = height;
        _forceKeyframe = true;
        _encoder = new H264Encoder(OpenH264Native.DllPath);
        if (_fast && TryInitializeFast(width, height))
        {
            _encoder.SetMaxBitrate(_bitrate);
            _encoder.SetTargetFps(_fps);
            return;
        }

        if (_fast)
        {
            // Fast setup failed: start over with a clean encoder instance.
            _encoder.Dispose();
            _encoder = new H264Encoder(OpenH264Native.DllPath);
            _fast = false;
        }
        _encoder.Initialize(width, height, _bitrate, _fps, ConfigType.ScreenCaptureAdvanced);
        _encoder.SetMaxBitrate(_bitrate);
        _encoder.SetTargetFps(_fps);
    }

    private bool TryInitializeFast(int width, int height)
    {
        try
        {
            var p = _encoder!.GetDefaultParameters();
            var threads = (ushort)Math.Clamp(Environment.ProcessorCount, 1, width * height >= 1920 * 1080 ? 8 : 4);

            p.iUsageType = EUsageType.SCREEN_CONTENT_REAL_TIME;
            p.iPicWidth = width;
            p.iPicHeight = height;
            p.iTargetBitrate = _bitrate;
            p.iMaxBitrate = _bitrate;
            p.iRCMode = RC_MODES.RC_BITRATE_MODE;
            p.fMaxFrameRate = _fps;
            p.iTemporalLayerNum = 1;
            p.iSpatialLayerNum = 1;
            p.bEnableFrameSkip = false;
            p.bEnableDenoise = false;
            p.bEnableBackgroundDetection = false;
            p.bEnableAdaptiveQuant = false;
            p.iComplexityMode = ECOMPLEXITY_MODE.LOW_COMPLEXITY;
            p.iMultipleThreadIdc = threads;
            p.uiIntraPeriod = 0; // keyframes are requested explicitly

            var layer = p.sSpatialLayers[0];
            layer.iVideoWidth = width;
            layer.iVideoHeight = height;
            layer.fFrameRate = _fps;
            layer.iSpatialBitrate = _bitrate;
            layer.iMaxSpatialBitrate = _bitrate;
            // One slice per thread (required for multi-threaded encoding).
            layer.sSliceArgument.uiSliceMode = threads > 1 ? SliceModeEnum.SM_FIXEDSLCNUM_SLICE : SliceModeEnum.SM_SINGLE_SLICE;
            layer.sSliceArgument.uiSliceNum = threads;
            // Declare a level that fits the picture (decoders may reject 4K signalled as level 4.0).
            if ((long)(width / 16) * (height / 16) > 8192)
                layer.uiLevelIdc = ELevelIdc.LEVEL_5_1;
            p.sSpatialLayers[0] = layer;

            return _encoder.Initialize(p) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsKeyframeNal(byte[] annexB)
    {
        for (var i = 0; i + 4 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                if ((annexB[i + 3] & 0x1F) is 5 or 7)
                    return true;
            }
            else if (i + 5 < annexB.Length && annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
            {
                if ((annexB[i + 4] & 0x1F) is 5 or 7)
                    return true;
            }
        }
        return false;
    }

    private static int Align16(int v) => Math.Max(16, v & ~15);

    public void Dispose()
    {
        _encoder?.Dispose();
        _encoder = null;
    }
}

public sealed class OpenH264Decoder : IDisposable
{
    private readonly H264Decoder _decoder;

    public OpenH264Decoder()
    {
        _decoder = new H264Decoder(OpenH264Native.DllPath);
        _decoder.Initialize();
    }

    public byte[]? DecodeAnnexB(byte[] annexB, int width, int height)
    {
        try
        {
            DecodingState state = default;
            var rgb = new RgbImage(Math.Max(16, width), Math.Max(16, height));
            if (!_decoder.Decode(annexB, 0, annexB.Length, true, out state, ref rgb))
                return null;

            var rgbBytes = rgb.GetBytes();
            if (rgbBytes is null || rgbBytes.Length == 0)
                return null;

            return RgbToBgra(rgbBytes, width, height);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] RgbToBgra(byte[] rgb, int width, int height)
    {
        if (rgb.Length >= width * height * 4)
        {
            // RGBA -> BGRA with opaque alpha: swap R/B in 32-bit lanes (fast path, parallel for large frames).
            var count = width * height;
            var bgra = new byte[count * 4];
            var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(rgb.AsSpan(0, count * 4));
            var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bgra.AsSpan());
            if (count < 1_000_000)
            {
                for (var i = 0; i < count; i++)
                {
                    var v = src[i];
                    dst[i] = (v & 0x0000FF00u) | ((v & 0xFFu) << 16) | ((v >> 16) & 0xFFu) | 0xFF000000u;
                }
            }
            else
            {
                var srcArr = rgb;
                Parallel.For(0, 8, part =>
                {
                    var from = (int)((long)count * part / 8);
                    var to = (int)((long)count * (part + 1) / 8);
                    var s32 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(srcArr.AsSpan(0, count * 4));
                    var d32 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bgra.AsSpan());
                    for (var i = from; i < to; i++)
                    {
                        var v = s32[i];
                        d32[i] = (v & 0x0000FF00u) | ((v & 0xFFu) << 16) | ((v >> 16) & 0xFFu) | 0xFF000000u;
                    }
                });
            }
            return bgra;
        }

        var dest = new byte[width * height * 4];
        var srcStride = rgb.Length / Math.Max(1, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var s = y * srcStride + x * 3;
                var d = (y * width + x) * 4;
                if (s + 2 >= rgb.Length) continue;
                dest[d] = rgb[s + 2];
                dest[d + 1] = rgb[s + 1];
                dest[d + 2] = rgb[s];
                dest[d + 3] = 255;
            }
        }
        return dest;
    }

    public void Dispose() => _decoder.Dispose();
}

public static class OpenH264Native
{
    public static string DllPath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            var name = "openh264-2.4.0-win64.dll";
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
                return path;
            var alt = Path.Combine(dir, "openh264.dll");
            return File.Exists(alt) ? alt : path;
        }
    }
}
