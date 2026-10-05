namespace RemoteSupport.Shared.Transport;

public sealed class PingMessage
{
    public string PingId { get; init; } = Guid.NewGuid().ToString("N");
    public long TimestampMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class PongMessage
{
    public string PingId { get; init; } = string.Empty;
    public long TimestampMs { get; init; }
}
