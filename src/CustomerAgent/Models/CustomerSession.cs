namespace CustomerAgent.Models;

public enum ConnectionState
{
    Offline,
    WaitingForCode,
    CodeActive,
    ConnectionRequested,
    ActiveSession,
    Disconnected
}

public class CustomerSession
{
    public string SupportCode { get; set; } = string.Empty;
    public DateTime? CodeExpiresAtUtc { get; set; }
    public Guid? ActiveSessionId { get; set; }
    public string? ConnectedAgentName { get; set; }
    public ConnectionState State { get; set; } = ConnectionState.Offline;
}
