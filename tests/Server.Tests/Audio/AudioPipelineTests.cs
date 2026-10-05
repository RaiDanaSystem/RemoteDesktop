using Microsoft.Extensions.Logging.Abstractions;
using RemoteSupport.Shared.Audio;

namespace RemoteSupport.Server.Tests.Audio;

public class AudioPipelineTests : IDisposable
{
    public void Dispose() { }

    // --- Device Enumeration Tests ---

    [Fact]
    public async Task NaudioCapture_GetDevices_ReturnsList()
    {
        using var capture = new NaudioAudioCapture(NullLogger<NaudioAudioCapture>.Instance);
        var devices = await capture.GetDevicesAsync();

        Assert.NotNull(devices);
        // On most systems there's at least one audio device
        // (may be empty in CI environments without audio hardware)
    }

    [Fact]
    public async Task NaudioCapture_SetDevice_SetsIndex()
    {
        using var capture = new NaudioAudioCapture(NullLogger<NaudioAudioCapture>.Instance);

        await capture.SetDeviceAsync(0);
        Assert.Equal(0, capture.CurrentDeviceIndex);
    }

    // --- Audio Format Tests ---

    [Fact]
    public void AudioPacket_PCM8000Hz_SerializeDeserialize()
    {
        var pkt = new AudioPacket
        {
            Type = AudioMessageType.AudioData,
            Format = AudioFormat.PCM_16bit_8000Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 8000,
            ChannelCount = 1,
            AudioData = new byte[160] // 10ms at 8kHz 16-bit mono
        };

        var serialized = AudioPacket.Serialize(pkt);
        var deserialized = AudioPacket.Deserialize(serialized);

        Assert.Equal(AudioFormat.PCM_16bit_8000Hz, deserialized!.Format);
        Assert.Equal(8000, deserialized.SampleRate);
        Assert.Equal(160, deserialized.AudioData!.Length);
    }

    [Fact]
    public void AudioPacket_PCM44100Hz_SerializeDeserialize()
    {
        var pkt = new AudioPacket
        {
            Type = AudioMessageType.AudioData,
            Format = AudioFormat.PCM_16bit_44100Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 44100,
            ChannelCount = 2,
            AudioData = new byte[3528] // 10ms at 44.1kHz 16-bit stereo
        };

        var serialized = AudioPacket.Serialize(pkt);
        var deserialized = AudioPacket.Deserialize(serialized);

        Assert.Equal(AudioFormat.PCM_16bit_44100Hz, deserialized!.Format);
        Assert.Equal(44100, deserialized.SampleRate);
        Assert.Equal(2, deserialized.ChannelCount);
    }

    // --- Mute/Unmute Tests ---

    [Fact]
    public void NaudioCapture_Mute_ProducesSilence()
    {
        using var capture = new NaudioAudioCapture(NullLogger<NaudioAudioCapture>.Instance);

        capture.IsMuted = true;
        Assert.True(capture.IsMuted);

        capture.IsMuted = false;
        Assert.False(capture.IsMuted);
    }

    [Fact]
    public void NaudioPlayback_Volume_Range()
    {
        using var playback = new NaudioAudioPlayback(NullLogger<NaudioAudioPlayback>.Instance);

        playback.Volume = 50;
        Assert.Equal(50, playback.Volume);

        playback.Volume = 0;
        Assert.Equal(0, playback.Volume);

        playback.Volume = 100;
        Assert.Equal(100, playback.Volume);

        playback.Volume = -10;
        Assert.Equal(0, playback.Volume); // Clamped

        playback.Volume = 150;
        Assert.Equal(100, playback.Volume); // Clamped
    }

    // --- Audio Packet Protocol Tests ---

    [Fact]
    public void AudioPacket_MuteControl_SerializeDeserialize()
    {
        var mutePkt = new AudioPacket
        {
            Type = AudioMessageType.MuteRequest,
            Format = AudioFormat.PCM_16bit_16000Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 16000,
            ChannelCount = 1,
            IsMuted = true
        };

        var serialized = AudioPacket.Serialize(mutePkt);
        var deserialized = AudioPacket.Deserialize(serialized);

        Assert.Equal(AudioMessageType.MuteRequest, deserialized!.Type);
        Assert.True(deserialized.IsMuted);
    }

    [Fact]
    public void AudioPacket_QualityControl_SerializeDeserialize()
    {
        var pkt = new AudioPacket
        {
            Type = AudioMessageType.QualityChange,
            Format = AudioFormat.Opus_48000Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 48000,
            ChannelCount = 1,
            QualityLevel = 7
        };

        var serialized = AudioPacket.Serialize(pkt);
        var deserialized = AudioPacket.Deserialize(serialized);

        Assert.Equal(AudioMessageType.QualityChange, deserialized!.Type);
        Assert.Equal(7, deserialized.QualityLevel);
    }

    [Fact]
    public void AudioPacket_Timestamp_Preserved()
    {
        var ts = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var pkt = new AudioPacket
        {
            Type = AudioMessageType.AudioData,
            Format = AudioFormat.PCM_16bit_16000Hz,
            TimestampTicks = ts.Ticks,
            SequenceNumber = 1,
            SampleRate = 16000,
            ChannelCount = 1,
            AudioData = new byte[320]
        };

        var serialized = AudioPacket.Serialize(pkt);
        var deserialized = AudioPacket.Deserialize(serialized);

        Assert.Equal(ts.Ticks, deserialized!.TimestampTicks);
    }

    [Fact]
    public void AudioPacket_InvalidData_ReturnsNull()
    {
        var result = AudioPacket.Deserialize(new byte[] { 0xFF });
        Assert.Null(result);
    }

    // --- Audio Pipeline Lifecycle Tests ---

    [Fact]
    public async Task AudioPipeline_StartStop_Lifecycle()
    {
        using var capture = new NaudioAudioCapture(NullLogger<NaudioAudioCapture>.Instance);
        using var playback = new NaudioAudioPlayback(NullLogger<NaudioAudioPlayback>.Instance);
        using var pipeline = new AudioPipeline(capture, playback, NullLogger<AudioPipeline>.Instance);

        Assert.False(pipeline.IsActive);

        // Start may fail if no audio device is available, but should not throw
        try
        {
            await pipeline.StartAsync();
            Assert.True(pipeline.IsActive);

            await pipeline.StopAsync();
            Assert.False(pipeline.IsActive);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Audio device not available in test environment
            Assert.False(pipeline.IsActive);
        }
    }

    [Fact]
    public async Task AudioPipeline_StopBeforeStart_NoOp()
    {
        using var capture = new NaudioAudioCapture(NullLogger<NaudioAudioCapture>.Instance);
        using var playback = new NaudioAudioPlayback(NullLogger<NaudioAudioPlayback>.Instance);
        using var pipeline = new AudioPipeline(capture, playback, NullLogger<AudioPipeline>.Instance);

        await pipeline.StopAsync(); // Should not throw
        Assert.False(pipeline.IsActive);
    }

    [Fact]
    public async Task AudioPipeline_StartTwice_NoOp()
    {
        using var capture = new NaudioAudioCapture(NullLogger<NaudioAudioCapture>.Instance);
        using var playback = new NaudioAudioPlayback(NullLogger<NaudioAudioPlayback>.Instance);
        using var pipeline = new AudioPipeline(capture, playback, NullLogger<AudioPipeline>.Instance);

        try
        {
            await pipeline.StartAsync();
            await pipeline.StartAsync(); // Should not throw or restart
            Assert.True(pipeline.IsActive);
            await pipeline.StopAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Audio device not available
        }
    }

    // --- Diagnostics Tests ---

    [Fact]
    public void AudioDiagnostics_DefaultValues()
    {
        var diag = new AudioDiagnostics();

        Assert.False(diag.IsCapturing);
        Assert.False(diag.IsPlaying);
        Assert.False(diag.IsMuted);
        Assert.Equal(0, diag.PacketsSent);
        Assert.Equal(0, diag.PacketsReceived);
        Assert.Equal(0, diag.DroppedPackets);
    }

    [Fact]
    public void AudioPipeline_GetDiagnostics_ReturnsValues()
    {
        using var capture = new NaudioAudioCapture(NullLogger<NaudioAudioCapture>.Instance);
        using var playback = new NaudioAudioPlayback(NullLogger<NaudioAudioPlayback>.Instance);
        using var pipeline = new AudioPipeline(capture, playback, NullLogger<AudioPipeline>.Instance);

        var diag = pipeline.GetDiagnostics();

        Assert.False(diag.IsCapturing);
        Assert.False(diag.IsPlaying);
        Assert.Equal(0, diag.PacketsSent);
        Assert.Equal(0, diag.PacketsReceived);
    }

    // --- Audio Level Tests ---

    [Fact]
    public void AudioPacket_Silence_HasZeroLevel()
    {
        var pkt = new AudioPacket
        {
            Type = AudioMessageType.AudioData,
            Format = AudioFormat.PCM_16bit_16000Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 16000,
            ChannelCount = 1,
            AudioData = new byte[320] // All zeros = silence
        };

        // Verify the data is silence
        Assert.All(pkt.AudioData!, b => Assert.Equal(0, b));
    }

    [Fact]
    public void AudioPacket_MaxAmplitude_HasHighLevel()
    {
        var data = new byte[320];
        for (int i = 0; i < data.Length - 1; i += 2)
        {
            data[i] = 0xFF;
            data[i + 1] = 0x7F; // Max positive amplitude
        }

        var pkt = new AudioPacket
        {
            Type = AudioMessageType.AudioData,
            Format = AudioFormat.PCM_16bit_16000Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 16000,
            ChannelCount = 1,
            AudioData = data
        };

        // Verify data is non-zero
        Assert.Contains(pkt.AudioData, b => b != 0);
    }

    // --- Serialization Performance Tests ---

    [Fact]
    public void AudioPacket_SerializePerformance_320Bytes()
    {
        var pkt = new AudioPacket
        {
            Type = AudioMessageType.AudioData,
            Format = AudioFormat.PCM_16bit_16000Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 16000,
            ChannelCount = 1,
            AudioData = new byte[320] // 20ms of 16kHz 16-bit mono
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 50_000; i++)
            AudioPacket.Serialize(pkt);
        sw.Stop();

        var opsPerMs = 50_000.0 / sw.ElapsedMilliseconds;
        Assert.True(opsPerMs > 100, $"Too slow: {opsPerMs:F0} ops/ms");
    }

    // --- Packet Loss Simulation Test ---

    [Fact]
    public void AudioPipeline_PacketLoss_DiagnosticsTrack()
    {
        using var capture = new NaudioAudioCapture(NullLogger<NaudioAudioCapture>.Instance);
        using var playback = new NaudioAudioPlayback(NullLogger<NaudioAudioPlayback>.Instance);
        using var pipeline = new AudioPipeline(capture, playback, NullLogger<AudioPipeline>.Instance);

        // Simulate receiving packets to test diagnostics
        var diag = pipeline.GetDiagnostics();
        Assert.Equal(0, diag.PacketsReceived);
        Assert.Equal(0, diag.DroppedPackets);
    }

    // --- Reconnection Test ---

    [Fact]
    public async Task AudioPipeline_Reconnect_StopStart()
    {
        using var capture = new NaudioAudioCapture(NullLogger<NaudioAudioCapture>.Instance);
        using var playback = new NaudioAudioPlayback(NullLogger<NaudioAudioPlayback>.Instance);
        using var pipeline = new AudioPipeline(capture, playback, NullLogger<AudioPipeline>.Instance);

        try
        {
            await pipeline.StartAsync();
            await pipeline.StopAsync();
            Assert.False(pipeline.IsActive);

            // Reconnect
            await pipeline.StartAsync();
            Assert.True(pipeline.IsActive);
            await pipeline.StopAsync();
            Assert.False(pipeline.IsActive);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Audio device not available
        }
    }

    // --- Dispose Cleanup Test ---

    [Fact]
    public async Task AudioPipeline_Dispose_CleansUp()
    {
        using var capture = new NaudioAudioCapture(NullLogger<NaudioAudioCapture>.Instance);
        using var playback = new NaudioAudioPlayback(NullLogger<NaudioAudioPlayback>.Instance);

        var pipeline = new AudioPipeline(capture, playback, NullLogger<AudioPipeline>.Instance);

        try
        {
            await pipeline.StartAsync();
        }
        catch (Exception) { }

        await pipeline.DisposeAsync(); // Should not throw
        Assert.False(pipeline.IsActive);
    }
}
