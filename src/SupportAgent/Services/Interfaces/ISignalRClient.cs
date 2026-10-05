namespace SupportAgent.Services.Interfaces;

public interface ISignalRClient : IAsyncDisposable
{
    Task StartAsync(string serverUrl, string accessToken, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task JoinSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task LeaveSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task RequestSessionStatusAsync(Guid sessionId, CancellationToken cancellationToken = default);
    bool IsConnected { get; }

    event Action<SessionRequestReceivedEventArgs>? SessionRequestReceived;
    event Action<SessionStateChangedEventArgs>? SessionStateChanged;
    event Action<SessionTerminatedEventArgs>? SessionTerminated;
    event Action<string>? ConnectionError;
    event Action? Connected;
    event Action? Disconnected;
}

public class SessionRequestReceivedEventArgs
{
    public Guid SessionId { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public string? AgentDisplayName { get; set; }
    public string AgentRole { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
}

public class SessionStateChangedEventArgs
{
    public Guid SessionId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? AgentName { get; set; }
    public string? AgentDisplayName { get; set; }
    public string? CustomerDeviceName { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string? EndReason { get; set; }
}

public class SessionTerminatedEventArgs
{
    public Guid SessionId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? EndReason { get; set; }
    public DateTime? EndedAtUtc { get; set; }
}
