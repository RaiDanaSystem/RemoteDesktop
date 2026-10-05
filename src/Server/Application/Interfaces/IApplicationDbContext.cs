using RemoteSupport.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace RemoteSupport.Server.Application.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<Device> Devices { get; }
    DbSet<SupportCode> SupportCodes { get; }
    DbSet<Session> Sessions { get; }
    DbSet<SessionEvent> SessionEvents { get; }
    DbSet<FileTransferMetadata> FileTransfers { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<CustomerSessionToken> CustomerSessionTokens { get; }
    DbSet<TransportToken> TransportTokens { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
