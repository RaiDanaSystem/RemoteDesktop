using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Application.Interfaces;

public interface IAuditService
{
    Task LogAsync(Guid? userId, AuditAction action, string? description = null, string? ipAddress = null, string? userAgent = null, CancellationToken cancellationToken = default);
}
