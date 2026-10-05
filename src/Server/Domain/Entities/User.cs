using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Domain.Entities;

public class User : EntityBase
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.SupportAgent;
    public bool IsActive { get; set; } = true;
    public string? DisplayName { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }

    public ICollection<Session> AgentSessions { get; set; } = new List<Session>();
    public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
