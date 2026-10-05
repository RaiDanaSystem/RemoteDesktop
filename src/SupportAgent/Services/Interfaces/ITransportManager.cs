namespace SupportAgent.Services.Interfaces;

public interface ITransportManager : IAsyncDisposable
{
    Task<TransportConnectionResult> ConnectAsync(string serverUrl, Guid sessionId, string transportToken, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<int> SendAsync(byte[] data, CancellationToken cancellationToken = default);
    TransportDiagnostics Diagnostics { get; }
    bool IsConnected { get; }
    TransportConnectionType ConnectionType { get; }
    event Action<byte[]>? DataReceived;
    event Action<string>? ConnectionStateChanged;
}

public enum TransportConnectionType
{
    None,
    P2P,
    Relay
}

public class TransportConnectionResult
{
    public bool IsSuccess { get; set; }
    public TransportConnectionType ConnectionType { get; set; }
    public string? ErrorMessage { get; set; }
}

public class TransportDiagnostics
{
    public TransportConnectionType ConnectionType { get; set; }
    public string ConnectionState { get; set; } = "Disconnected";
    public double LatencyMs { get; set; }
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
    public int ReconnectCount { get; set; }
    public string? FailureReason { get; set; }
    public DateTime? ConnectedAtUtc { get; set; }
}
