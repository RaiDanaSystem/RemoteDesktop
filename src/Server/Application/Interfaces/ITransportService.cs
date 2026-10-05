namespace RemoteSupport.Server.Application.Interfaces;

public interface ITransportService
{
    Task<TransportTokenResult> IssueTransportTokenAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default);
    Task<TransportTokenResult> IssueCustomerTransportTokenAsync(Guid sessionId, string deviceIdentifier, CancellationToken cancellationToken = default);
    Task<TransportValidationResult> ValidateTransportTokenAsync(string token, CancellationToken cancellationToken = default);
    Task RevokeTransportTokensAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public class TransportTokenResult
{
    public bool IsSuccess { get; set; }
    public string? Token { get; set; }
    public byte[]? SessionKey { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
}

public class TransportValidationResult
{
    public bool IsValid { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? UserId { get; set; }
    public string? DeviceIdentifier { get; set; }
    public byte[]? SessionKey { get; set; }
    public string? ErrorMessage { get; set; }
}
