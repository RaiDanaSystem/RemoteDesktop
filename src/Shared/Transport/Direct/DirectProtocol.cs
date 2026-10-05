using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace RemoteSupport.Shared.Transport.Direct;

/// <summary>
/// Server-less LAN transport ("direct mode").
///
/// TCP stream framing: [uint32 LE length][byte tag][body]  (length = 1 + body length)
///   tag 0x01 Hello   (viewer → host)   JSON <see cref="HelloMessage"/>
///   tag 0x02 Accept  (host → viewer)   JSON <see cref="AcceptMessage"/>
///   tag 0x03 Reject  (host → viewer)   JSON <see cref="RejectMessage"/>
///   tag 0x10 Data    (both)            a serialized <see cref="TransportEnvelope"/>, i.e. exactly what
///                                       the WebRTC data channel carries, so all feature managers work unchanged.
///
/// Discovery: UDP broadcast on <see cref="DiscoveryPort"/>; hosts send a <see cref="BeaconMessage"/> JSON
/// every couple of seconds and answer "probe" datagrams immediately.
/// </summary>
public static class DirectProtocol
{
    public const string ProtocolId = "RDLAN1";
    public const int DefaultTcpPort = 45870;
    public const int DiscoveryPort = 45871;
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    public const byte TagHello = 0x01;
    public const byte TagAccept = 0x02;
    public const byte TagReject = 0x03;
    public const byte TagData = 0x10;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static byte[] Frame(byte tag, ReadOnlySpan<byte> body)
    {
        var buf = new byte[4 + 1 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)(1 + body.Length));
        buf[4] = tag;
        body.CopyTo(buf.AsSpan(5));
        return buf;
    }

    public static byte[] FrameJson<T>(byte tag, T message)
        => Frame(tag, JsonSerializer.SerializeToUtf8Bytes(message, Json));

    public static async Task<(byte Tag, byte[] Body)?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct)) return null;
        var len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len < 1 || len > MaxFrameBytes) return null;
        var payload = new byte[len];
        if (!await ReadExactAsync(stream, payload, ct)) return null;
        return (payload[0], payload.AsSpan(1).ToArray());
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public static T? Parse<T>(byte[] body)
    {
        try { return JsonSerializer.Deserialize<T>(body, Json); }
        catch { return default; }
    }

    public static string Utf8(byte[] data) => Encoding.UTF8.GetString(data);
}

public sealed class HelloMessage
{
    public string Protocol { get; set; } = DirectProtocol.ProtocolId;
    public string ViewerId { get; set; } = string.Empty;
    public string ViewerName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
}

public sealed class AcceptMessage
{
    public string HostName { get; set; } = string.Empty;
    public bool ViewOnly { get; set; }
}

public sealed class RejectMessage
{
    public string Reason { get; set; } = "Rejected";
}

public sealed class BeaconMessage
{
    public string App { get; set; } = DirectProtocol.ProtocolId;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Platform { get; set; } = "Windows";
    public int Port { get; set; } = DirectProtocol.DefaultTcpPort;
    public bool Sharing { get; set; } = true;
    /// <summary>True for a viewer's "who is out there?" datagram (hosts answer immediately).</summary>
    public bool Probe { get; set; }
}

public sealed class LanPeer
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public System.Net.IPAddress Address { get; init; } = System.Net.IPAddress.None;
    public int Port { get; init; }
    public DateTime LastSeenUtc { get; set; }
    public override string ToString() => $"{Name} ({Address}:{Port})";
}
