using Microsoft.EntityFrameworkCore;
using RemoteSupport.Server.Application.Interfaces;
using RemoteSupport.Server.Domain.Entities;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Application.Services;

public class SessionService : ISessionService
{
    private readonly IApplicationDbContext _context;

    public SessionService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Session> CreateSessionAsync(Guid agentUserId, string customerDeviceIdentifier, string? customerDeviceName, string? customerOperatingSystem, CancellationToken cancellationToken = default)
    {
        var session = new Session
        {
            Id = Guid.NewGuid(),
            AgentUserId = agentUserId,
            CustomerDeviceIdentifier = customerDeviceIdentifier,
            CustomerDeviceName = customerDeviceName,
            CustomerOperatingSystem = customerOperatingSystem,
            Status = SessionStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.Sessions.Add(session);
        await _context.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<Session?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return await _context.Sessions.FindAsync(new object[] { sessionId }, cancellationToken);
    }

    public async Task<IReadOnlyList<Session>> GetActiveSessionsAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Sessions
            .Where(s => s.Status == SessionStatus.Active || s.Status == SessionStatus.Pending)
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<Session?> UpdateSessionStatusAsync(Guid sessionId, SessionStatus status, string? endReason = null, CancellationToken cancellationToken = default)
    {
        var session = await _context.Sessions.FindAsync(new object[] { sessionId }, cancellationToken);
        if (session is null) return null;

        session.Status = status;
        session.UpdatedAtUtc = DateTime.UtcNow;

        if (status == SessionStatus.Ended)
        {
            session.EndedAtUtc = DateTime.UtcNow;
            session.EndReason = endReason;
        }

        if (status == SessionStatus.Active)
        {
            session.StartedAtUtc = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<SessionEvent> AddSessionEventAsync(Guid sessionId, SessionEventType eventType, string? details = null, CancellationToken cancellationToken = default)
    {
        var sessionEvent = new SessionEvent
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            EventType = eventType,
            Details = details,
            OccurredAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.SessionEvents.Add(sessionEvent);
        await _context.SaveChangesAsync(cancellationToken);
        return sessionEvent;
    }
}
