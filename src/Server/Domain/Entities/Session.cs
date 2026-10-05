using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Domain.Entities;

public class Session : EntityBase
{
    public Guid AgentUserId { get; set; }
    public User AgentUser { get; set; } = null!;

    public string CustomerDeviceIdentifier { get; set; } = string.Empty;
    public string? CustomerDeviceName { get; set; }
    public string? CustomerOperatingSystem { get; set; }

    public SessionStatus Status { get; set; } = SessionStatus.Pending;
    public ConnectionType ConnectionType { get; set; }
    public string? PeerConnectionId { get; set; }
    public string? RelayConnectionId { get; set; }

    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string? EndReason { get; set; }

    public ICollection<SessionEvent> Events { get; set; } = new List<SessionEvent>();
    public ICollection<FileTransferMetadata> FileTransfers { get; set; } = new List<FileTransferMetadata>();
}
