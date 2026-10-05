using RemoteSupport.Server.Domain.Entities;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Application.Interfaces;

public interface ISessionService
{
    Task<Session> CreateSessionAsync(Guid agentUserId, string customerDeviceIdentifier, string? customerDeviceName, string? customerOperatingSystem, CancellationToken cancellationToken = default);
    Task<Session?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Session>> GetActiveSessionsAsync(CancellationToken cancellationToken = default);
    Task<Session?> UpdateSessionStatusAsync(Guid sessionId, SessionStatus status, string? endReason = null, CancellationToken cancellationToken = default);
    Task<SessionEvent> AddSessionEventAsync(Guid sessionId, SessionEventType eventType, string? details = null, CancellationToken cancellationToken = default);
}
