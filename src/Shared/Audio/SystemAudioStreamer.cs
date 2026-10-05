using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RemoteSupport.Shared.Audio;

/// <summary>
/// Captures what this PC is playing (WASAPI loopback) and emits it as 16-bit PCM <see cref="AudioPacket"/>s
/// (about 20 ms each) in the device's native sample rate, stereo (or mono). Nothing is sent while the PC is silent.
/// </summary>
public sealed class SystemAudioStreamer : IDisposable
{
    private readonly ILogger? _logger;
    private WasapiLoopbackCapture? _capture;
    private uint _sequence;
    private int _sampleRate;
    private int _channels;
    private int _bytesPerFrameIn;
    private bool _isFloat;
    private int _bitsIn;

    // Accumulates ~20 ms of converted audio per packet.
    private byte[] _chunk = Array.Empty<byte>();
    private int _chunkFill;

    public bool IsRunning { get; private set; }

    private long _peakTick;
    private int _peakValue;

    /// <summary>Largest sample (0..1) in the audio captured during the last ~1.5 s; 0 when nothing was captured.</summary>
    public double RecentPeak =>
        Environment.TickCount64 - Interlocked.Read(ref _peakTick) > 1500 ? 0 : Volatile.Read(ref _peakValue) / 32768.0;
    public int SampleRate => _sampleRate;
    public int Channels => _channels;

    /// <summary>Serialized <see cref="AudioPacket"/> ready to be sent as a TransportMessageType.Audio payload.</summary>
    public event Action<byte[]>? PacketReady;

    public SystemAudioStreamer(ILogger? logger = null) => _logger = logger;

    public void Start()
    {
        if (IsRunning) return;
        try
        {
            _capture = new WasapiLoopbackCapture();
            var f = _capture.WaveFormat;
            _sampleRate = f.SampleRate;
            _channels = Math.Min(2, f.Channels);
            _bitsIn = f.BitsPerSample;
            _bytesPerFrameIn = f.BlockAlign;
            _isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
                       || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32);
            var samplesPer20Ms = _sampleRate / 50;
            _chunk = new byte[samplesPer20Ms * _channels * 2];
            _chunkFill = 0;

            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) _logger?.LogWarning(e.Exception, "Loopback capture stopped");
                IsRunning = false;
            };
            _capture.StartRecording();
            IsRunning = true;
            _logger?.LogInformation("System audio streaming started: {Rate} Hz, {Ch} ch (device format {Bits} bit)",
                _sampleRate, _channels, _bitsIn);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not start system audio capture");
            Stop();
        }
    }

    public void Stop()
    {
        IsRunning = false;
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (!IsRunning || e.BytesRecorded <= 0) return;

        var frames = e.BytesRecorded / _bytesPerFrameIn;
        var chIn = Math.Max(1, _bytesPerFrameIn / Math.Max(1, _bitsIn / 8));
        var bytesPerSample = _bitsIn / 8;
        var buf = e.Buffer;

        for (var i = 0; i < frames; i++)
        {
            var frameOffset = i * _bytesPerFrameIn;
            for (var c = 0; c < _channels; c++)
            {
                var off = frameOffset + Math.Min(c, chIn - 1) * bytesPerSample;
                short s16;
                if (_isFloat && bytesPerSample == 4)
                {
                    var v = BitConverter.ToSingle(buf, off);
                    s16 = (short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue);
                }
                else if (bytesPerSample == 2)
                {
                    s16 = BitConverter.ToInt16(buf, off);
                }
                else if (bytesPerSample == 3)
                {
                    s16 = (short)((buf[off + 2] << 8) | buf[off + 1]);
                }
                else if (bytesPerSample == 4)
                {
                    s16 = (short)(BitConverter.ToInt32(buf, off) >> 16);
                }
                else
                {
                    s16 = 0;
                }

                var abs = s16 < 0 ? -s16 : s16;
                if (abs > _peakValue || Environment.TickCount64 - _peakTick > 1500)
                {
                    _peakValue = abs;
                    Interlocked.Exchange(ref _peakTick, Environment.TickCount64);
                }
                _chunk[_chunkFill++] = (byte)(s16 & 0xFF);
                _chunk[_chunkFill++] = (byte)((s16 >> 8) & 0xFF);
            }

            if (_chunkFill >= _chunk.Length)
            {
                Emit();
                _chunkFill = 0;
            }
        }
    }

    private void Emit()
    {
        var data = new byte[_chunk.Length];
        Buffer.BlockCopy(_chunk, 0, data, 0, data.Length);
        var packet = new AudioPacket
        {
            Type = AudioMessageType.AudioData,
            Format = AudioFormat.PCM_16bit_44100Hz, // informational; SampleRate/ChannelCount below are authoritative
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = ++_sequence,
            SampleRate = _sampleRate,
            ChannelCount = _channels,
            AudioData = data
        };
        PacketReady?.Invoke(AudioPacket.Serialize(packet));
    }

    public void Dispose() => Stop();
}
