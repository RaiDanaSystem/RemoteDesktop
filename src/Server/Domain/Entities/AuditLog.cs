using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Domain.Entities;

public class AuditLog : EntityBase
{
    public Guid? UserId { get; set; }
    public User? User { get; set; }
    public AuditAction Action { get; set; }
    public string? Description { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}
