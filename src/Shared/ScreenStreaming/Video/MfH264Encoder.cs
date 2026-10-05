using System.Runtime.InteropServices;
using SharpDX.MediaFoundation;

namespace RemoteSupport.Shared.ScreenStreaming.Video;

public sealed class MfH264Encoder : IDisposable
{
    private static readonly Guid EncoderClsid = new("6ca50344-051a-4ded-9779-a43305165e35");
    private static readonly object StartupLock = new();
    private static bool _started;

    private Transform? _mft;
    private int _width;
    private int _height;
    private readonly int _fps;
    private readonly int _bitrate;
    private long _frameIndex;
    private byte[] _spsPps = [];
    private bool _forceNextKeyframe = true;

    public MfH264Encoder(int width, int height, int fps = 15, int bitrate = 1_500_000)
    {
        EnsureStartup();
        _fps = Math.Clamp(fps, 5, 30);
        _bitrate = Math.Clamp(bitrate, 250_000, 8_000_000);
        Reconfigure(Align16(width), Align16(height));
    }

    public static bool TryCreate(int width, int height, int fps, int bitrate, out MfH264Encoder? encoder)
    {
        encoder = null;
        try
        {
            encoder = new MfH264Encoder(width, height, fps, bitrate);
            return true;
        }
        catch
        {
            encoder?.Dispose();
            encoder = null;
            return false;
        }
    }

    public void EnsureSize(int width, int height)
    {
        width = Align16(width);
        height = Align16(height);
        if (width == _width && height == _height)
            return;
        Reconfigure(width, height);
    }

    public void RequestKeyframe() => _forceNextKeyframe = true;

    public byte[]? EncodeBgra(byte[] bgra, int width, int height, out bool keyframe)
    {
        keyframe = false;
        width = Align16(width);
        height = Align16(height);
        EnsureSize(width, height);
        if (_mft is null)
            return null;

        var nv12 = YuvConvert.BgraToNv12(bgra, width, height);
        using var sample = MediaFactory.CreateSample();
        using var buffer = MediaFactory.CreateMemoryBuffer(nv12.Length);
        var ptr = buffer.Lock(out _, out _);
        try
        {
            Marshal.Copy(nv12, 0, ptr, nv12.Length);
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = nv12.Length;
        sample.AddBuffer(buffer);
        sample.SampleTime = _frameIndex * 10_000_000L / _fps;
        sample.SampleDuration = 10_000_000L / _fps;

        if (_forceNextKeyframe)
        {
            TryForceKeyframe();
            _forceNextKeyframe = false;
            keyframe = true;
        }

        _mft.ProcessInput(0, sample, 0);
        _frameIndex++;

        var annexB = DrainOutput();
        if (annexB is null || annexB.Length == 0)
            return null;

        if (IsKeyframeNal(annexB))
            keyframe = true;

        if (keyframe && _spsPps.Length > 0 && !HasSps(annexB))
        {
            var combined = new byte[_spsPps.Length + annexB.Length];
            Buffer.BlockCopy(_spsPps, 0, combined, 0, _spsPps.Length);
            Buffer.BlockCopy(annexB, 0, combined, _spsPps.Length, annexB.Length);
            return combined;
        }

        return annexB;
    }

    private byte[]? DrainOutput()
    {
        if (_mft is null) return null;

        _mft.GetOutputStreamInfo(0, out var info);
        var providesSamples = ((MftOutputStreamInformationFlags)info.DwFlags & MftOutputStreamInformationFlags.MftOutputStreamProvidesSamples) != 0;

        Sample? clientSample = null;
        MediaBuffer? clientBuffer = null;
        if (!providesSamples)
        {
            var size = Math.Max(info.CbSize, 64 * 1024);
            clientSample = MediaFactory.CreateSample();
            clientBuffer = MediaFactory.CreateMemoryBuffer(size);
            clientSample.AddBuffer(clientBuffer);
        }

        var buffers = new[]
        {
            new TOutputDataBuffer
            {
                DwStreamID = 0,
                PSample = clientSample
            }
        };

        try
        {
            _mft.ProcessOutput(TransformProcessOutputFlags.None, buffers, out _);
        }
        catch (SharpDX.SharpDXException ex) when (
            ex.ResultCode.Code == unchecked((int)0xC00D6D72) ||
            ex.ResultCode.Code == unchecked((int)0xC00D36E6))
        {
            clientBuffer?.Dispose();
            clientSample?.Dispose();
            return null;
        }

        var sample = buffers[0].PSample ?? clientSample;
        if (sample is null)
        {
            clientBuffer?.Dispose();
            return null;
        }

        try
        {
            using var resultBuffer = sample.ConvertToContiguousBuffer();
            var dataPtr = resultBuffer.Lock(out var maxLen, out var currentLen);
            try
            {
                var length = currentLen > 0 ? currentLen : maxLen;
                if (length <= 0)
                    return null;
                var bytes = new byte[length];
                Marshal.Copy(dataPtr, bytes, 0, length);
                return ToAnnexB(bytes);
            }
            finally
            {
                resultBuffer.Unlock();
            }
        }
        finally
        {
            if (!ReferenceEquals(sample, clientSample))
                sample.Dispose();
            clientBuffer?.Dispose();
            clientSample?.Dispose();
        }
    }

    private void Reconfigure(int width, int height)
    {
        DisposeMft();
        _width = width;
        _height = height;
        _frameIndex = 0;
        _forceNextKeyframe = true;

        _mft = new Transform(EncoderClsid);

        using var outputType = CreateVideoType(VideoFormatGuids.H264, width, height);
        _mft.SetOutputType(0, outputType, 0);

        using var inputType = CreateVideoType(VideoFormatGuids.NV12, width, height);
        _mft.SetInputType(0, inputType, 0);

        TryConfigureCodec();

        _mft.ProcessMessage(TMessageType.CommandFlush, IntPtr.Zero);
        _mft.ProcessMessage(TMessageType.NotifyBeginStreaming, IntPtr.Zero);
        _mft.ProcessMessage(TMessageType.NotifyStartOfStream, IntPtr.Zero);
        _spsPps = [];
    }

    private void TryConfigureCodec() { }

    private void TryForceKeyframe() { }

    private static byte[] ToAnnexB(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1)))
            return data;

        using var ms = new MemoryStream();
        var offset = 0;
        while (offset + 4 <= data.Length)
        {
            var naluLen = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            offset += 4;
            if (naluLen <= 0 || offset + naluLen > data.Length)
                break;
            ms.WriteByte(0);
            ms.WriteByte(0);
            ms.WriteByte(0);
            ms.WriteByte(1);
            ms.Write(data, offset, naluLen);
            offset += naluLen;
        }

        return ms.Length > 0 ? ms.ToArray() : data;
    }

    private static bool IsKeyframeNal(byte[] annexB)
    {
        var i = 0;
        while (i + 4 < annexB.Length)
        {
            var start = FindStartCode(annexB, i, out var scLen);
            if (start < 0) return false;
            var nalType = annexB[start + scLen] & 0x1F;
            if (nalType == 5) return true;
            i = start + scLen + 1;
        }
        return false;
    }

    private static bool HasSps(byte[] annexB)
    {
        var i = 0;
        while (i + 4 < annexB.Length)
        {
            var start = FindStartCode(annexB, i, out var scLen);
            if (start < 0) return false;
            if ((annexB[start + scLen] & 0x1F) == 7) return true;
            i = start + scLen + 1;
        }
        return false;
    }

    private static int FindStartCode(byte[] data, int from, out int length)
    {
        length = 0;
        for (var i = from; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                length = 3;
                return i;
            }
            if (i + 4 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
            {
                length = 4;
                return i;
            }
        }
        return -1;
    }

    private MediaType CreateVideoType(Guid subtype, int width, int height)
    {
        var type = new MediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, subtype);
        type.Set(MfMtFrameSize, PackSize(width, height));
        type.Set(MfMtFrameRate, PackSize(_fps, 1));
        type.Set(MfMtInterlaceMode, 2);
        if (subtype == VideoFormatGuids.H264)
        {
            type.Set(MfMtMpeg2Profile, 66);
            type.Set(MfMtAvgBitrate, _bitrate);
        }
        return type;
    }

    private static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MfMtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MfMtInterlaceMode = new("83bc64da-36e2-4ecf-9b72-79961558118b");
    private static readonly Guid MfMtMpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MfMtAvgBitrate = new("20332624-fb0d-4d9e-bd0d-cb93332bb612");

    private static long PackSize(int a, int b) => ((long)(uint)a << 32) | (uint)b;

    private static int Align16(int v) => Math.Max(16, v & ~15);

    private static void EnsureStartup()
    {
        lock (StartupLock)
        {
            if (_started) return;
            MediaManager.Startup();
            _started = true;
        }
    }

    private void DisposeMft()
    {
        if (_mft is null) return;
        try
        {
            _mft.ProcessMessage(TMessageType.NotifyEndOfStream, IntPtr.Zero);
            _mft.ProcessMessage(TMessageType.CommandFlush, IntPtr.Zero);
        }
        catch { }
        _mft.Dispose();
        _mft = null;
    }

    public void Dispose() => DisposeMft();
}

