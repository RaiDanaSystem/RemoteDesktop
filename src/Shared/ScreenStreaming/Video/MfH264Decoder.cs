using System.Runtime.InteropServices;
using SharpDX.MediaFoundation;

namespace RemoteSupport.Shared.ScreenStreaming.Video;

public sealed class MfH264Decoder : IDisposable
{
    private static readonly Guid DecoderClsid = new("62ce7e72-4c71-4d20-b15d-452831a87d9d");
    private static readonly object StartupLock = new();
    private static bool _started;

    private Transform? _mft;
    private int _width;
    private int _height;

    public MfH264Decoder()
    {
        lock (StartupLock)
        {
            if (!_started)
            {
                MediaManager.Startup();
                _started = true;
            }
        }
    }

    public byte[]? DecodeAnnexB(byte[] annexB, int width, int height)
    {
        width = Math.Max(16, width & ~15);
        height = Math.Max(16, height & ~15);
        EnsureConfigured(width, height);
        if (_mft is null)
            return null;

        using var sample = MediaFactory.CreateSample();
        using var buffer = MediaFactory.CreateMemoryBuffer(annexB.Length);
        var ptr = buffer.Lock(out _, out _);
        try
        {
            Marshal.Copy(annexB, 0, ptr, annexB.Length);
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = annexB.Length;
        sample.AddBuffer(buffer);

        _mft.ProcessInput(0, sample, 0);
        return DrainNv12ToBgra();
    }

    private byte[]? DrainNv12ToBgra()
    {
        if (_mft is null) return null;
        var nv12Size = _width * _height * 3 / 2;

        _mft.GetOutputStreamInfo(0, out var info);
        var providesSamples = ((MftOutputStreamInformationFlags)info.DwFlags & MftOutputStreamInformationFlags.MftOutputStreamProvidesSamples) != 0;

        Sample? clientSample = null;
        MediaBuffer? clientBuffer = null;
        if (!providesSamples)
        {
            var size = Math.Max(Math.Max(info.CbSize, nv12Size), 1024);
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
                if (length < nv12Size)
                    return null;
                var nv12 = new byte[nv12Size];
                Marshal.Copy(dataPtr, nv12, 0, nv12Size);
                return YuvConvert.Nv12ToBgra(nv12, _width, _height);
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

    private void EnsureConfigured(int width, int height)
    {
        if (_mft is not null && _width == width && _height == height)
            return;

        DisposeMft();
        _width = width;
        _height = height;
        _mft = new Transform(DecoderClsid);

        using var inputType = new MediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        inputType.Set(MediaTypeAttributeKeys.FrameSize, ((long)width << 32) | (uint)height);
        inputType.Set(MediaTypeAttributeKeys.FrameRate, ((long)15 << 32) | 1u);
        _mft.SetInputType(0, inputType, 0);

        using var outputType = new MediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        outputType.Set(MediaTypeAttributeKeys.FrameSize, ((long)width << 32) | (uint)height);
        outputType.Set(MediaTypeAttributeKeys.FrameRate, ((long)15 << 32) | 1u);
        _mft.SetOutputType(0, outputType, 0);

        _mft.ProcessMessage(TMessageType.NotifyBeginStreaming, IntPtr.Zero);
        _mft.ProcessMessage(TMessageType.NotifyStartOfStream, IntPtr.Zero);
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
