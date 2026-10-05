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

    public OpenH264Encoder(int width, int height, int fps = 15, int bitrate = 1_500_000)
    {
        _fps = Math.Clamp(fps, 5, 30);
        _bitrate = Math.Clamp(bitrate, 250_000, 8_000_000);
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

        var image = new ImageData(ImageType.Rgba, width, height, width * 4, bgra);
        if (!_encoder.Encode(image, out EncodedData[]? nalus) || nalus is null || nalus.Length == 0)
            return null;

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
        _encoder.Initialize(width, height, _bitrate, _fps, ConfigType.ScreenCaptureAdvanced);
        _encoder.SetMaxBitrate(_bitrate);
        _encoder.SetTargetFps(_fps);
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
            var bgra = new byte[width * height * 4];
            for (var i = 0; i < width * height; i++)
            {
                var s = i * 4;
                var d = i * 4;
                bgra[d] = rgb[s + 2];
                bgra[d + 1] = rgb[s + 1];
                bgra[d + 2] = rgb[s];
                bgra[d + 3] = 255;
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
