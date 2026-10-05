using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RemoteSupport.Shared.Audio;

public class AudioPipeline : IAsyncDisposable, IDisposable
{
    private readonly IAudioCapture _capture;
    private readonly IAudioPlayback _playback;
    private readonly ILogger<AudioPipeline> _logger;
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private bool _isActive;
    private long _packetsSent;
    private long _packetsReceived;
    private int _droppedPackets;
    private readonly ConcurrentQueue<AudioPacket> _jitterBuffer = new();
    private readonly Queue<double> _levelSamples = new();
    private const int MaxJitterBufferSize = 50;
    private const int JitterBufferDelayMs = 20;

    public event Action<AudioPacket>? AudioPacketReady;
    public event Action<double>? AudioLevelChanged;
    public bool IsActive => _isActive;

    public AudioPipeline(IAudioCapture capture, IAudioPlayback playback, ILogger<AudioPipeline> logger)
    {
        _capture = capture;
        _playback = playback;
        _logger = logger;
    }

    public async Task StartAsync(AudioFormat format = AudioFormat.PCM_16bit_16000Hz, CancellationToken cancellationToken = default)
    {
        if (_isActive) return;

        _isActive = true;
        _cts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);

        await _capture.StartCaptureAsync(format, linkedCts.Token);
        await _playback.StartPlaybackAsync(format, linkedCts.Token);

        _capture.AudioCaptured += OnAudioCaptured;

        _captureTask = Task.Run(async () => await CaptureLoop(linkedCts.Token), linkedCts.Token);

        _logger.LogInformation("Audio pipeline started");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isActive) return;

        _isActive = false;
        _capture.AudioCaptured -= OnAudioCaptured;
        _cts?.Cancel();

        if (_captureTask is not null)
        {
            try { await Task.WhenAny(_captureTask, Task.Delay(2000, cancellationToken)); }
            catch { }
        }

        await _capture.StopCaptureAsync(cancellationToken);
        await _playback.StopPlaybackAsync(cancellationToken);

        _logger.LogInformation("Audio pipeline stopped. Sent: {Sent}, Received: {Received}, Dropped: {Dropped}",
            _packetsSent, _packetsReceived, _droppedPackets);
    }

    public async Task ReceiveAudioPacketAsync(AudioPacket packet, CancellationToken cancellationToken = default)
    {
        if (!_isActive || packet.AudioData is null) return;

        if (_jitterBuffer.Count < MaxJitterBufferSize)
        {
            _jitterBuffer.Enqueue(packet);
            Interlocked.Increment(ref _packetsReceived);
        }
        else
        {
            Interlocked.Increment(ref _droppedPackets);
        }

        while (_jitterBuffer.TryDequeue(out var pkt))
        {
            await _playback.PlayAudioAsync(pkt, cancellationToken);
        }
    }

    public AudioDiagnostics GetDiagnostics()
    {
        return new AudioDiagnostics
        {
            IsCapturing = _capture.IsCapturing,
            IsPlaying = _playback.IsPlaying,
            IsMuted = _capture.IsMuted,
            DeviceIndex = _capture.CurrentDeviceIndex,
            Volume = _playback.Volume,
            PacketsSent = _packetsSent,
            PacketsReceived = _packetsReceived,
            DroppedPackets = _droppedPackets,
            StartedAtUtc = _isActive ? DateTime.UtcNow : null
        };
    }

    public void SetMute(bool muted)
    {
        _capture.IsMuted = muted;
    }

    public void SetVolume(int volume)
    {
        _playback.Volume = volume;
    }

    private void OnAudioCaptured(AudioPacket packet)
    {
        if (!_isActive) return;

        Interlocked.Increment(ref _packetsSent);
        AudioPacketReady?.Invoke(packet);
    }

    private async Task CaptureLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _isActive)
        {
            try
            {
                var packet = await _capture.CaptureAudioAsync(cancellationToken);
                if (packet is not null)
                {
                    TrackAudioLevel(packet.AudioData);
                }

                await Task.Delay(5, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Capture loop error");
            }
        }
    }

    private void TrackAudioLevel(byte[]? audioData)
    {
        if (audioData is null || audioData.Length == 0) return;

        double sum = 0;
        for (int i = 0; i < audioData.Length - 1; i += 2)
        {
            var sample = BitConverter.ToInt16(audioData, i);
            sum += sample * sample;
        }

        var rms = Math.Sqrt(sum / (audioData.Length / 2));
        var level = Math.Min(rms / 32768.0, 1.0);

        _levelSamples.Enqueue(level);
        if (_levelSamples.Count > 10) _levelSamples.Dequeue();

        var avgLevel = _levelSamples.Average();
        AudioLevelChanged?.Invoke(avgLevel);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Dispose();
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
