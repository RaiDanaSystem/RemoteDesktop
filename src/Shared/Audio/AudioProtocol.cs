namespace RemoteSupport.Shared.Audio;

public enum AudioFormat : byte
{
    PCM_16bit_8000Hz = 0x01,
    PCM_16bit_16000Hz = 0x02,
    PCM_16bit_44100Hz = 0x03,
    Opus_8000Hz = 0x10,
    Opus_16000Hz = 0x11,
    Opus_48000Hz = 0x12
}

public enum AudioMessageType : byte
{
    AudioData = 0x01,
    MuteRequest = 0x02,
    UnmuteRequest = 0x03,
    QualityChange = 0x04,
    DeviceInfo = 0x05
}

public class AudioPacket
{
    public AudioMessageType Type { get; set; }
    public AudioFormat Format { get; set; }
    public long TimestampTicks { get; set; }
    public uint SequenceNumber { get; set; }
    public int SampleRate { get; set; }
    public int ChannelCount { get; set; }
    public byte[]? AudioData { get; set; }

    // Mute/quality
    public bool IsMuted { get; set; }
    public int QualityLevel { get; set; } // 1-10

    public static byte[] Serialize(AudioPacket pkt)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)pkt.Type);
        writer.Write((byte)pkt.Format);
        writer.Write(pkt.TimestampTicks);
        writer.Write(pkt.SequenceNumber);
        writer.Write(pkt.SampleRate);
        writer.Write(pkt.ChannelCount);

        if (pkt.Type == AudioMessageType.AudioData && pkt.AudioData is not null)
        {
            writer.Write(pkt.AudioData.Length);
            writer.Write(pkt.AudioData);
        }
        else if (pkt.Type == AudioMessageType.MuteRequest || pkt.Type == AudioMessageType.UnmuteRequest)
        {
            writer.Write(pkt.IsMuted);
        }
        else if (pkt.Type == AudioMessageType.QualityChange)
        {
            writer.Write(pkt.QualityLevel);
        }

        return ms.ToArray();
    }

    public static AudioPacket? Deserialize(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            var pkt = new AudioPacket
            {
                Type = (AudioMessageType)reader.ReadByte(),
                Format = (AudioFormat)reader.ReadByte(),
                TimestampTicks = reader.ReadInt64(),
                SequenceNumber = reader.ReadUInt32(),
                SampleRate = reader.ReadInt32(),
                ChannelCount = reader.ReadInt32()
            };

            if (pkt.Type == AudioMessageType.AudioData)
            {
                var dataLen = reader.ReadInt32();
                if (dataLen > 0 && dataLen < 1_000_000)
                    pkt.AudioData = reader.ReadBytes(dataLen);
            }
            else if (pkt.Type == AudioMessageType.MuteRequest || pkt.Type == AudioMessageType.UnmuteRequest)
            {
                pkt.IsMuted = reader.ReadBoolean();
            }
            else if (pkt.Type == AudioMessageType.QualityChange)
            {
                pkt.QualityLevel = reader.ReadInt32();
            }

            return pkt;
        }
        catch
        {
            return null;
        }
    }
}

public interface IAudioCapture : IAsyncDisposable
{
    Task StartCaptureAsync(AudioFormat format = AudioFormat.PCM_16bit_16000Hz, CancellationToken cancellationToken = default);
    Task StopCaptureAsync(CancellationToken cancellationToken = default);
    Task<AudioPacket?> CaptureAudioAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AudioDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default);
    Task SetDeviceAsync(int deviceIndex, CancellationToken cancellationToken = default);
    bool IsCapturing { get; }
    bool IsMuted { get; set; }
    int CurrentDeviceIndex { get; }
    event Action<AudioPacket>? AudioCaptured;
}

public interface IAudioPlayback : IAsyncDisposable
{
    Task StartPlaybackAsync(AudioFormat format = AudioFormat.PCM_16bit_16000Hz, CancellationToken cancellationToken = default);
    Task StopPlaybackAsync(CancellationToken cancellationToken = default);
    Task PlayAudioAsync(AudioPacket packet, CancellationToken cancellationToken = default);
    bool IsPlaying { get; set; }
    int Volume { get; set; } // 0-100
}

public record AudioDeviceInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
}

public record AudioDiagnostics
{
    public bool IsCapturing { get; set; }
    public bool IsPlaying { get; set; }
    public bool IsMuted { get; set; }
    public int DeviceIndex { get; set; }
    public int Volume { get; set; }
    public AudioFormat Format { get; set; }
    public long PacketsSent { get; set; }
    public long PacketsReceived { get; set; }
    public int DroppedPackets { get; set; }
    public DateTime? StartedAtUtc { get; set; }
}
