using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace RemoteSupport.Shared.Audio;

public class NaudioAudioPlayback : IAudioPlayback, IDisposable
{
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _waveProvider;
    private readonly ILogger<NaudioAudioPlayback> _logger;
    private bool _isPlaying;
    private int _volume = 80;
    private AudioFormat _currentFormat;
    private readonly ConcurrentQueue<AudioPacket> _playbackQueue = new();
    private Task? _playbackTask;
    private CancellationTokenSource? _playbackCts;

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            _isPlaying = value;
            if (_waveOut is not null)
                if (value) _waveOut.Play(); else _waveOut.Pause();
        }
    }

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            if (_waveOut is not null)
                _waveOut.Volume = _volume / 100.0f;
        }
    }

    public NaudioAudioPlayback(ILogger<NaudioAudioPlayback> logger)
    {
        _logger = logger;
    }

    public Task StartPlaybackAsync(AudioFormat format = AudioFormat.PCM_16bit_16000Hz, CancellationToken cancellationToken = default)
    {
        if (_isPlaying) return Task.CompletedTask;

        _currentFormat = format;
        var waveFormat = GetWaveFormat(format);

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 3,
            Volume = _volume / 100.0f
        };

        _waveProvider = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true
        };

        _waveOut.Init(_waveProvider);
        _waveOut.Play();

        _isPlaying = true;
        _playbackCts = new CancellationTokenSource();
        _playbackTask = Task.Run(() => PlaybackLoop(_playbackCts.Token), _playbackCts.Token);

        _logger.LogInformation("Audio playback started: {Format}", format);
        return Task.CompletedTask;
    }

    public Task StopPlaybackAsync(CancellationToken cancellationToken = default)
    {
        if (!_isPlaying) return Task.CompletedTask;

        _isPlaying = false;
        _playbackCts?.Cancel();
        _waveOut?.Stop();
        _logger.LogInformation("Audio playback stopped");
        return Task.CompletedTask;
    }

    public Task PlayAudioAsync(AudioPacket packet, CancellationToken cancellationToken = default)
    {
        if (!_isPlaying || packet.AudioData is null || _waveProvider is null)
            return Task.CompletedTask;

        try
        {
            var sampleRate = GetSampleRate(packet.Format);
            var channelCount = GetChannelCount(packet.Format);
            var expectedFormat = new WaveFormat(sampleRate, 16, channelCount);

            if (_waveProvider.WaveFormat.SampleRate != sampleRate ||
                _waveProvider.WaveFormat.Channels != channelCount)
            {
                _logger.LogWarning("Format mismatch: expected {Expected}, got {Actual}",
                    $"{sampleRate}Hz {channelCount}ch",
                    $"{_waveProvider.WaveFormat.SampleRate}Hz {_waveProvider.WaveFormat.Channels}ch");
                return Task.CompletedTask;
            }

            _waveProvider.AddSamples(packet.AudioData, 0, packet.AudioData.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error playing audio packet");
        }

        return Task.CompletedTask;
    }

    private void PlaybackLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _isPlaying)
        {
            try
            {
                if (_waveProvider is not null && _waveOut is not null)
                {
                    var buffered = _waveProvider.BufferedBytes;
                    var desired = _waveProvider.WaveFormat.AverageBytesPerSecond / 4;

                    if (buffered < desired / 2 && _waveOut.PlaybackState == PlaybackState.Playing)
                    {
                        _waveOut.Pause();
                    }
                    else if (buffered >= desired && _waveOut.PlaybackState == PlaybackState.Paused)
                    {
                        _waveOut.Play();
                    }
                }

                Thread.Sleep(10);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Playback loop error");
            }
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
        await StopPlaybackAsync();
        Dispose();
    }

    public void Dispose()
    {
        _playbackCts?.Dispose();
        _waveOut?.Dispose();
        _waveOut = null;
    }
}
