namespace RemoteSupport.Server.Domain.Entities;

public class Device : EntityBase
{
    public string DeviceIdentifier { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public Guid? UserId { get; set; }
    public User? User { get; set; }
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}
