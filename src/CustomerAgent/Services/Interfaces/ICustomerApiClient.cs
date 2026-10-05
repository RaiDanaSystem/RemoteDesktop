namespace CustomerAgent.Services.Interfaces;

public interface ICustomerApiClient
{
    Task<SupportCodeResult> GenerateSupportCodeAsync(string deviceIdentifier, CancellationToken cancellationToken = default);
    Task<ConnectionRequestResult> CheckConnectionRequestAsync(string supportCode, CancellationToken cancellationToken = default);
    Task<bool> AcceptConnectionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<bool> RejectConnectionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<string?> GetDeviceTokenAsync(string deviceIdentifier, CancellationToken cancellationToken = default);
}

public class SupportCodeResult
{
    public bool IsSuccess { get; set; }
    public string? Code { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ConnectionRequestResult
{
    public bool HasPendingRequest { get; set; }
    public Guid? SessionId { get; set; }
    public string? AgentName { get; set; }
    public string? AgentRole { get; set; }
    public string? ErrorMessage { get; set; }
}
