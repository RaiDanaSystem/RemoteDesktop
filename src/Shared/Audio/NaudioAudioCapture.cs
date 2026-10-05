using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace RemoteSupport.Shared.Audio;

public class NaudioAudioCapture : IAudioCapture, IDisposable
{
    private WaveInEvent? _waveIn;
    private readonly ILogger<NaudioAudioCapture> _logger;
    private bool _isCapturing;
    private bool _isMuted;
    private int _currentDeviceIndex;
    private uint _sequenceNumber;
    private AudioFormat _currentFormat;
    private readonly Queue<AudioPacket> _captureBuffer = new();
    private readonly object _bufferLock = new();

    public bool IsCapturing => _isCapturing;
    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }
    public int CurrentDeviceIndex => _currentDeviceIndex;

    public event Action<AudioPacket>? AudioCaptured;

    public NaudioAudioCapture(ILogger<NaudioAudioCapture> logger)
    {
        _logger = logger;
    }

    public Task StartCaptureAsync(AudioFormat format = AudioFormat.PCM_16bit_16000Hz, CancellationToken cancellationToken = default)
    {
        if (_isCapturing)
            return Task.CompletedTask;

        _currentFormat = format;
        var waveFormat = GetWaveFormat(format);

        _waveIn = new WaveInEvent
        {
            DeviceNumber = _currentDeviceIndex,
            WaveFormat = waveFormat,
            BufferMilliseconds = 20,
            NumberOfBuffers = 3
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        try
        {
            _waveIn.StartRecording();
            _isCapturing = true;
            _logger.LogInformation("Audio capture started: {Format}, device {Device}",
                format, _currentDeviceIndex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start audio capture");
        }

        return Task.CompletedTask;
    }

    public Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        if (!_isCapturing) return Task.CompletedTask;

        _isCapturing = false;
        _waveIn?.StopRecording();
        _logger.LogInformation("Audio capture stopped");
        return Task.CompletedTask;
    }

    public Task<AudioPacket?> CaptureAudioAsync(CancellationToken cancellationToken = default)
    {
        lock (_bufferLock)
        {
            if (_captureBuffer.Count > 0)
            {
                return Task.FromResult<AudioPacket?>(_captureBuffer.Dequeue());
            }
        }

        return Task.FromResult<AudioPacket?>(null);
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<AudioDeviceInfo>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            devices.Add(new AudioDeviceInfo
            {
                Index = i,
                Name = caps.ProductName,
                IsDefault = i == 0
            });
        }
        return Task.FromResult<IReadOnlyList<AudioDeviceInfo>>(devices);
    }

    public Task SetDeviceAsync(int deviceIndex, CancellationToken cancellationToken = default)
    {
        if (deviceIndex >= 0 && deviceIndex < WaveInEvent.DeviceCount)
        {
            _currentDeviceIndex = deviceIndex;
        }
        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isCapturing) return;

        var format = _currentFormat;
        var sampleRate = GetSampleRate(format);
        var channelCount = GetChannelCount(format);

        byte[] audioData;
        if (_isMuted)
        {
            audioData = new byte[e.BytesRecorded];
        }
        else
        {
            audioData = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, audioData, 0, e.BytesRecorded);
        }

        var packet = new AudioPacket
        {
            Type = AudioMessageType.AudioData,
            Format = format,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
            SampleRate = sampleRate,
            ChannelCount = channelCount,
            AudioData = audioData
        };

        lock (_bufferLock)
        {
            if (_captureBuffer.Count < 100)
            {
                _captureBuffer.Enqueue(packet);
            }
        }

        AudioCaptured?.Invoke(packet);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _logger.LogWarning(e.Exception, "Recording stopped with error");
        }
    }

    private static WaveFormat GetWaveFormat(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.PCM_16bit_8000Hz => new WaveFormat(8000, 16, 1),
            AudioFormat.PCM_16bit_16000Hz => new WaveFormat(16000, 16, 1),
            AudioFormat.PCM_16bit_44100Hz => new WaveFormat(44100, 16, 2),
            _ => new WaveFormat(16000, 16, 1)
        };
    }

    private static int GetSampleRate(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.PCM_16bit_8000Hz => 8000,
            AudioFormat.PCM_16bit_16000Hz => 16000,
            AudioFormat.PCM_16bit_44100Hz => 44100,
            _ => 16000
        };
    }

    private static int GetChannelCount(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.PCM_16bit_8000Hz => 1,
            AudioFormat.PCM_16bit_16000Hz => 1,
            AudioFormat.PCM_16bit_44100Hz => 2,
            _ => 1
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        Dispose();
    }

    public void Dispose()
    {
        _waveIn?.Dispose();
        _waveIn = null;
    }
}
