namespace RemoteSupport.Server.Domain.Entities;

public class SupportCode : EntityBase
{
    public string Code { get; set; } = string.Empty;
    public string DeviceIdentifier { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public bool IsUsed { get; set; }
    public Guid? UsedBySessionId { get; set; }
    public Session? UsedBySession { get; set; }
}
