using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Domain.Entities;

public class SessionEvent : EntityBase
{
    public Guid SessionId { get; set; }
    public Session Session { get; set; } = null!;
    public SessionEventType EventType { get; set; }
    public string? Details { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}
