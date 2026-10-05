namespace RemoteSupport.Server.Domain.Entities;

public class FileTransferMetadata : EntityBase
{
    public Guid SessionId { get; set; }
    public Session Session { get; set; } = null!;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public bool IsUpload { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsFailed { get; set; }
    public string? FailureReason { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
