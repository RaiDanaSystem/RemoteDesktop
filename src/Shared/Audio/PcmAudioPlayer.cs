using NAudio.Wave;

namespace RemoteSupport.Shared.Audio;

/// <summary>Plays streamed 16-bit PCM <see cref="AudioPacket"/>s (system audio from the remote PC).</summary>
public sealed class PcmAudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private int _rate;
    private int _channels;
    private bool _started;
    private readonly object _lock = new();

    public void Play(AudioPacket packet)
    {
        if (packet.Type != AudioMessageType.AudioData || packet.AudioData is null || packet.AudioData.Length == 0)
            return;
        if (packet.SampleRate < 8000 || packet.SampleRate > 96000 || packet.ChannelCount is < 1 or > 2)
            return;

        lock (_lock)
        {
            if (_buffer is null || _rate != packet.SampleRate || _channels != packet.ChannelCount)
                Recreate(packet.SampleRate, packet.ChannelCount);

            // Keep latency low: if more than ~300 ms is waiting, drop the new packet.
            var maxBytes = _rate * _channels * 2 * 3 / 10;
            if (_buffer!.BufferedBytes > maxBytes) return;

            _buffer.AddSamples(packet.AudioData, 0, packet.AudioData.Length);
            if (!_started && _buffer.BufferedBytes >= _rate * _channels * 2 / 12) // ~80 ms prebuffer
            {
                _output!.Play();
                _started = true;
            }
        }
    }

    private void Recreate(int rate, int channels)
    {
        DisposeOutput();
        _rate = rate;
        _channels = channels;
        _buffer = new BufferedWaveProvider(new WaveFormat(rate, 16, channels))
        {
            BufferDuration = TimeSpan.FromMilliseconds(600),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        _output = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 };
        _output.Init(_buffer);
        _started = false;
    }

    private void DisposeOutput()
    {
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        _output = null;
        _buffer = null;
    }

    public void Dispose()
    {
        lock (_lock) DisposeOutput();
    }
}
