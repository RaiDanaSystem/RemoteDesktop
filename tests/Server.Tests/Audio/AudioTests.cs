using System.Security.Cryptography;
using RemoteSupport.Shared.Audio;

namespace RemoteSupport.Server.Tests.Audio;

public class AudioTests
{
    // --- AudioPacket Serialization Tests ---

    [Fact]
    public void AudioPacket_AudioData_SerializeDeserialize()
    {
        var audioData = new byte[] { 0x01, 0x02, 0x03, 0xFF, 0xFE };
        var pkt = new AudioPacket
        {
            Type = AudioMessageType.AudioData,
            Format = AudioFormat.PCM_16bit_16000Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 16000,
            ChannelCount = 1,
            AudioData = audioData
        };

        var serialized = AudioPacket.Serialize(pkt);
        var deserialized = AudioPacket.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(AudioMessageType.AudioData, deserialized!.Type);
        Assert.Equal(AudioFormat.PCM_16bit_16000Hz, deserialized.Format);
        Assert.Equal(16000, deserialized.SampleRate);
        Assert.Equal(1, deserialized.ChannelCount);
        Assert.Equal(audioData, deserialized.AudioData);
    }

    [Fact]
    public void AudioPacket_MuteRequest_SerializeDeserialize()
    {
        var pkt = new AudioPacket
        {
            Type = AudioMessageType.MuteRequest,
            Format = AudioFormat.PCM_16bit_16000Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 16000,
            ChannelCount = 1,
            IsMuted = true
        };

        var serialized = AudioPacket.Serialize(pkt);
        var deserialized = AudioPacket.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(AudioMessageType.MuteRequest, deserialized!.Type);
        Assert.True(deserialized.IsMuted);
    }

    [Fact]
    public void AudioPacket_UnmuteRequest_SerializeDeserialize()
    {
        var pkt = new AudioPacket
        {
            Type = AudioMessageType.UnmuteRequest,
            Format = AudioFormat.PCM_16bit_16000Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 16000,
            ChannelCount = 1,
            IsMuted = false
        };

        var serialized = AudioPacket.Serialize(pkt);
        var deserialized = AudioPacket.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(AudioMessageType.UnmuteRequest, deserialized!.Type);
        Assert.False(deserialized.IsMuted);
    }

    [Fact]
    public void AudioPacket_QualityChange_SerializeDeserialize()
    {
        var pkt = new AudioPacket
        {
            Type = AudioMessageType.QualityChange,
            Format = AudioFormat.Opus_16000Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 16000,
            ChannelCount = 1,
            QualityLevel = 8
        };

        var serialized = AudioPacket.Serialize(pkt);
        var deserialized = AudioPacket.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(AudioMessageType.QualityChange, deserialized!.Type);
        Assert.Equal(8, deserialized.QualityLevel);
    }

    [Fact]
    public void AudioPacket_AllFormats_SerializeDeserialize()
    {
        foreach (var format in Enum.GetValues<AudioFormat>())
        {
            var pkt = new AudioPacket
            {
                Type = AudioMessageType.AudioData,
                Format = format,
                TimestampTicks = DateTime.UtcNow.Ticks,
                SequenceNumber = 1,
                SampleRate = 16000,
                ChannelCount = 1,
                AudioData = new byte[] { 0x01 }
            };

            var serialized = AudioPacket.Serialize(pkt);
            var deserialized = AudioPacket.Deserialize(serialized);

            Assert.Equal(format, deserialized!.Format);
        }
    }

    [Fact]
    public void AudioPacket_AllMessageTypes_SerializeDeserialize()
    {
        foreach (var type in Enum.GetValues<AudioMessageType>())
        {
            var pkt = new AudioPacket
            {
                Type = type,
                Format = AudioFormat.PCM_16bit_16000Hz,
                TimestampTicks = DateTime.UtcNow.Ticks,
                SequenceNumber = 1,
                SampleRate = 16000,
                ChannelCount = 1,
                AudioData = type == AudioMessageType.AudioData ? new byte[] { 0x01 } : null,
                IsMuted = type == AudioMessageType.MuteRequest,
                QualityLevel = type == AudioMessageType.QualityChange ? 5 : 0
            };

            var serialized = AudioPacket.Serialize(pkt);
            var deserialized = AudioPacket.Deserialize(serialized);

            Assert.NotNull(deserialized);
            Assert.Equal(type, deserialized!.Type);
        }
    }

    [Fact]
    public void AudioPacket_InvalidData_ReturnsNull()
    {
        var result = AudioPacket.Deserialize(new byte[] { 0xFF, 0xFF });
        Assert.Null(result);
    }

    [Fact]
    public void AudioPacket_LargeAudioData_SerializeDeserialize()
    {
        var largeData = new byte[32000]; // 1 second of 16kHz 16-bit mono PCM
        RandomNumberGenerator.Fill(largeData);

        var pkt = new AudioPacket
        {
            Type = AudioMessageType.AudioData,
            Format = AudioFormat.PCM_16bit_16000Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 42,
            SampleRate = 16000,
            ChannelCount = 1,
            AudioData = largeData
        };

        var serialized = AudioPacket.Serialize(pkt);
        var deserialized = AudioPacket.Deserialize(serialized);

        Assert.Equal(largeData, deserialized!.AudioData);
        Assert.Equal(32000, deserialized.AudioData!.Length);
    }

    [Fact]
    public void AudioPacket_SequenceNumber_Preserved()
    {
        for (uint i = 0; i < 1000; i++)
        {
            var pkt = new AudioPacket
            {
                Type = AudioMessageType.AudioData,
                Format = AudioFormat.PCM_16bit_16000Hz,
                TimestampTicks = DateTime.UtcNow.Ticks,
                SequenceNumber = i,
                SampleRate = 16000,
                ChannelCount = 1,
                AudioData = new byte[] { (byte)(i % 256) }
            };

            var serialized = AudioPacket.Serialize(pkt);
            var deserialized = AudioPacket.Deserialize(serialized);

            Assert.Equal(i, deserialized!.SequenceNumber);
        }
    }

    [Fact]
    public void AudioPacket_StereoAudio_SerializeDeserialize()
    {
        var pkt = new AudioPacket
        {
            Type = AudioMessageType.AudioData,
            Format = AudioFormat.PCM_16bit_44100Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 44100,
            ChannelCount = 2,
            AudioData = new byte[1000]
        };

        var serialized = AudioPacket.Serialize(pkt);
        var deserialized = AudioPacket.Deserialize(serialized);

        Assert.Equal(2, deserialized!.ChannelCount);
        Assert.Equal(44100, deserialized.SampleRate);
    }

    // --- Performance Tests ---

    [Fact]
    public void AudioPacket_SerializePerformance()
    {
        var pkt = new AudioPacket
        {
            Type = AudioMessageType.AudioData,
            Format = AudioFormat.PCM_16bit_16000Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 16000,
            ChannelCount = 1,
            AudioData = new byte[320]
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
            AudioPacket.Serialize(pkt);
        sw.Stop();

        var opsPerMs = 10_000.0 / sw.ElapsedMilliseconds;
        Assert.True(opsPerMs > 50, $"Audio serialize too slow: {opsPerMs:F0} ops/ms");
    }

    [Fact]
    public void AudioPacket_DeserializePerformance()
    {
        var pkt = new AudioPacket
        {
            Type = AudioMessageType.AudioData,
            Format = AudioFormat.PCM_16bit_16000Hz,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            SampleRate = 16000,
            ChannelCount = 1,
            AudioData = new byte[320]
        };
        var serialized = AudioPacket.Serialize(pkt);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
            AudioPacket.Deserialize(serialized);
        sw.Stop();

        var opsPerMs = 10_000.0 / sw.ElapsedMilliseconds;
        Assert.True(opsPerMs > 50, $"Audio deserialize too slow: {opsPerMs:F0} ops/ms");
    }
}
