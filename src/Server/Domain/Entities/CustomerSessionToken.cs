namespace RemoteSupport.Server.Domain.Entities;

public class CustomerSessionToken : EntityBase
{
    public string TokenHash { get; set; } = string.Empty;
    public string DeviceIdentifier { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? OperatingSystem { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public bool IsRevoked { get; set; }
    public Guid? LinkedSessionId { get; set; }
    public Session? LinkedSession { get; set; }
}
