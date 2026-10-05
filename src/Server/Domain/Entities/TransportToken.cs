namespace RemoteSupport.Server.Domain.Entities;

public class TransportToken : EntityBase
{
    public Guid SessionId { get; set; }
    public Session Session { get; set; } = null!;
    public Guid? UserId { get; set; }
    public User? User { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public byte[] EncryptedKey { get; set; } = [];
    public DateTime ExpiresAtUtc { get; set; }
    public bool IsRevoked { get; set; }
    public string? ConnectionType { get; set; }
}
