using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RemoteSupport.Server.Application.Interfaces;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Application.Services;

public class SessionManager : ISessionManager
{
    private readonly IApplicationDbContext _context;
    private readonly ISessionService _sessionService;
    private readonly ISupportCodeService _supportCodeService;
    private readonly IAuditService _auditService;
    private readonly ISessionEventNotifier? _notifier;
    private readonly ILogger<SessionManager>? _logger;

    public SessionManager(
        IApplicationDbContext context,
        ISessionService sessionService,
        ISupportCodeService supportCodeService,
        IAuditService auditService,
        ISessionEventNotifier? notifier = null,
        ILogger<SessionManager>? logger = null)
    {
        _context = context;
        _sessionService = sessionService;
        _supportCodeService = supportCodeService;
        _auditService = auditService;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<SessionInitiationResult> InitiateConnectionAsync(Guid agentUserId, string supportCode, CancellationToken cancellationToken = default)
    {
        var user = await _context.Users.FindAsync(new object[] { agentUserId }, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return new SessionInitiationResult { IsSuccess = false, ErrorMessage = "User not found or inactive." };
        }

        var validatedCode = await _supportCodeService.ValidateCodeAsync(supportCode, cancellationToken);
        if (validatedCode is null)
        {
            await _auditService.LogAsync(agentUserId, AuditAction.SessionCreated,
                $"Attempted to use invalid/expired support code: {supportCode}",
                null, null, cancellationToken);

            return new SessionInitiationResult { IsSuccess = false, ErrorMessage = "Invalid or expired support code." };
        }

        var existingSessions = await _context.Sessions
            .Where(s =>
                s.CustomerDeviceIdentifier == validatedCode.DeviceIdentifier &&
                (s.Status == SessionStatus.Pending || s.Status == SessionStatus.Active))
            .ToListAsync(cancellationToken);

        foreach (var existingSession in existingSessions)
        {
            await _sessionService.UpdateSessionStatusAsync(
                existingSession.Id, SessionStatus.Ended,
                "Replaced by a new connection request",
                cancellationToken);
            await _sessionService.AddSessionEventAsync(
                existingSession.Id, SessionEventType.SessionEnded,
                "Superseded by a new support connection",
                cancellationToken);
            _logger?.LogInformation(
                "Ended previous {Status} session {SessionId} for device {Device} so a new connection can start",
                existingSession.Status, existingSession.Id, validatedCode.DeviceIdentifier);
        }

        var session = await _sessionService.CreateSessionAsync(
            agentUserId,
            validatedCode.DeviceIdentifier,
            validatedCode.DeviceIdentifier, // Use identifier as display name
            null, cancellationToken);

        validatedCode.IsUsed = true;
        validatedCode.UsedBySessionId = session.Id;
        await _context.SaveChangesAsync(cancellationToken);

        await _sessionService.AddSessionEventAsync(
            session.Id, SessionEventType.ConnectionRequested,
            $"Support agent {user.Username} initiated connection with code {supportCode}",
            cancellationToken);

        await _auditService.LogAsync(agentUserId, AuditAction.SessionCreated,
            $"Session {session.Id} created for device {validatedCode.DeviceIdentifier}",
            null, null, cancellationToken);

        if (_notifier is not null)
        {
            await _notifier.NotifyConnectionRequestCreated(
                agentUserId, validatedCode.DeviceIdentifier, session.Id,
                user.Username, user.DisplayName, user.Role.ToString(), session.CreatedAtUtc);
        }

        return new SessionInitiationResult
        {
            IsSuccess = true,
            SessionId = session.Id,
            CustomerDeviceName = session.CustomerDeviceName,
            CustomerOperatingSystem = session.CustomerOperatingSystem
        };
    }

    public async Task<ConnectionRequestPollResult> GetPendingConnectionRequestAsync(string deviceIdentifier, CancellationToken cancellationToken = default)
    {
        var pendingSession = await _context.Sessions
            .Include(s => s.AgentUser)
            .FirstOrDefaultAsync(s =>
                s.CustomerDeviceIdentifier == deviceIdentifier &&
                s.Status == SessionStatus.Pending,
                cancellationToken);

        if (pendingSession is null)
        {
            return new ConnectionRequestPollResult { HasPendingRequest = false };
        }

        return new ConnectionRequestPollResult
        {
            HasPendingRequest = true,
            SessionId = pendingSession.Id,
            AgentUserId = pendingSession.AgentUserId,
            AgentName = pendingSession.AgentUser.Username,
            AgentDisplayName = pendingSession.AgentUser.DisplayName,
            AgentRole = pendingSession.AgentUser.Role.ToString(),
            RequestedAtUtc = pendingSession.CreatedAtUtc
        };
    }

    public async Task<SessionAcceptResult> AcceptConnectionAsync(Guid sessionId, string customerDeviceIdentifier, CancellationToken cancellationToken = default)
    {
        var session = await _context.Sessions
            .Include(s => s.AgentUser)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return new SessionAcceptResult { IsSuccess = false, ErrorMessage = "Session not found." };
        }

        if (session.CustomerDeviceIdentifier != customerDeviceIdentifier)
        {
            await _auditService.LogAsync(null, AuditAction.SecurityEvent,
                $"Device {customerDeviceIdentifier} attempted to accept session {sessionId} belonging to {session.CustomerDeviceIdentifier}",
                null, null, cancellationToken);
            return new SessionAcceptResult { IsSuccess = false, ErrorMessage = "Unauthorized." };
        }

        if (session.Status != SessionStatus.Pending)
        {
            return new SessionAcceptResult { IsSuccess = false, ErrorMessage = "Session is not in pending state." };
        }

        await _sessionService.UpdateSessionStatusAsync(sessionId, SessionStatus.Active, cancellationToken: cancellationToken);
        await _sessionService.AddSessionEventAsync(sessionId, SessionEventType.ConnectionAccepted,
            "Customer accepted the connection", cancellationToken);

        await _auditService.LogAsync(null, AuditAction.SessionConnected,
            $"Customer {customerDeviceIdentifier} accepted session {sessionId}",
            null, null, cancellationToken);

        if (_notifier is not null)
        {
            await _notifier.NotifySessionAccepted(
                session.AgentUserId, customerDeviceIdentifier, sessionId,
                SessionStatus.Active.ToString(),
                session.AgentUser.Username, session.AgentUser.DisplayName,
                session.CustomerDeviceName);
        }

        return new SessionAcceptResult { IsSuccess = true };
    }

    public async Task<SessionRejectResult> RejectConnectionAsync(Guid sessionId, string customerDeviceIdentifier, CancellationToken cancellationToken = default)
    {
        var session = await _context.Sessions
            .Include(s => s.AgentUser)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return new SessionRejectResult { IsSuccess = false, ErrorMessage = "Session not found." };
        }

        if (session.CustomerDeviceIdentifier != customerDeviceIdentifier)
        {
            await _auditService.LogAsync(null, AuditAction.SecurityEvent,
                $"Device {customerDeviceIdentifier} attempted to reject session {sessionId} belonging to {session.CustomerDeviceIdentifier}",
                null, null, cancellationToken);
            return new SessionRejectResult { IsSuccess = false, ErrorMessage = "Unauthorized." };
        }

        if (session.Status != SessionStatus.Pending)
        {
            return new SessionRejectResult { IsSuccess = false, ErrorMessage = "Session is not in pending state." };
        }

        await _sessionService.UpdateSessionStatusAsync(sessionId, SessionStatus.Ended, "Customer rejected", cancellationToken);
        await _sessionService.AddSessionEventAsync(sessionId, SessionEventType.ConnectionRejected,
            "Customer rejected the connection", cancellationToken);

        await _auditService.LogAsync(null, AuditAction.SessionDisconnected,
            $"Customer {customerDeviceIdentifier} rejected session {sessionId}",
            null, null, cancellationToken);

        if (_notifier is not null)
        {
            await _notifier.NotifySessionRejected(
                session.AgentUserId, customerDeviceIdentifier, sessionId,
                SessionStatus.Ended.ToString(), "Customer rejected");
        }

        return new SessionRejectResult { IsSuccess = true };
    }

    public async Task<SessionStatusResult> GetSessionStatusAsync(Guid sessionId, Guid? userId, string? deviceIdentifier, CancellationToken cancellationToken = default)
    {
        var session = await _context.Sessions
            .Include(s => s.AgentUser)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return new SessionStatusResult { SessionExists = false };
        }

        if (userId.HasValue && session.AgentUserId != userId.Value)
        {
            return new SessionStatusResult { SessionExists = false };
        }

        if (!string.IsNullOrEmpty(deviceIdentifier) && session.CustomerDeviceIdentifier != deviceIdentifier)
        {
            return new SessionStatusResult { SessionExists = false };
        }

        return new SessionStatusResult
        {
            SessionExists = true,
            SessionId = session.Id,
            Status = session.Status.ToString(),
            AgentUserId = session.AgentUserId,
            AgentName = session.AgentUser.Username,
            AgentDisplayName = session.AgentUser.DisplayName,
            CustomerDeviceName = session.CustomerDeviceName,
            CustomerDeviceIdentifier = session.CustomerDeviceIdentifier,
            StartedAtUtc = session.StartedAtUtc,
            EndedAtUtc = session.EndedAtUtc,
            EndReason = session.EndReason
        };
    }

    public async Task<TerminationResult> TerminateSessionAsync(Guid sessionId, Guid? userId, string? deviceIdentifier, string? reason, CancellationToken cancellationToken = default)
    {
        var session = await _context.Sessions
            .Include(s => s.AgentUser)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return new TerminationResult { IsSuccess = false, ErrorMessage = "Session not found." };
        }

        bool isAgent = userId.HasValue && session.AgentUserId == userId.Value;
        bool isCustomer = !string.IsNullOrEmpty(deviceIdentifier) && session.CustomerDeviceIdentifier == deviceIdentifier;

        if (!isAgent && !isCustomer)
        {
            await _auditService.LogAsync(userId, AuditAction.SecurityEvent,
                $"Unauthorized termination attempt on session {sessionId}",
                null, null, cancellationToken);
            return new TerminationResult { IsSuccess = false, ErrorMessage = "Unauthorized." };
        }

        if (session.Status == SessionStatus.Ended)
        {
            return new TerminationResult { IsSuccess = false, ErrorMessage = "Session already ended." };
        }

        var terminatedBy = isAgent ? $"Agent {session.AgentUser.Username}" : $"Customer {deviceIdentifier}";
        var endReasonVal = reason ?? $"Terminated by {terminatedBy}";

        await _sessionService.UpdateSessionStatusAsync(sessionId, SessionStatus.Ended, endReasonVal, cancellationToken);
        await _sessionService.AddSessionEventAsync(sessionId, SessionEventType.SessionEnded,
            endReasonVal, cancellationToken);

        var linkedCodes = await _context.SupportCodes
            .Where(c => c.UsedBySessionId == sessionId)
            .ToListAsync(cancellationToken);
        foreach (var code in linkedCodes)
        {
            code.IsUsed = false;
            code.UsedBySessionId = null;
        }
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(userId, AuditAction.SessionDisconnected,
            $"Session {sessionId} terminated by {terminatedBy}: {endReasonVal}",
            null, null, cancellationToken);

        if (_notifier is not null)
        {
            await _notifier.NotifySessionTerminated(
                session.AgentUserId, session.CustomerDeviceIdentifier, sessionId,
                SessionStatus.Ended.ToString(), endReasonVal, DateTime.UtcNow);
        }

        return new TerminationResult { IsSuccess = true };
    }

    public async Task<int> TerminateOpenSessionsForDeviceAsync(
        string deviceIdentifier, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceIdentifier))
            return 0;

        var openSessions = await _context.Sessions
            .Where(s =>
                s.CustomerDeviceIdentifier == deviceIdentifier &&
                (s.Status == SessionStatus.Pending || s.Status == SessionStatus.Active))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        var ended = 0;
        foreach (var sessionId in openSessions)
        {
            var result = await TerminateSessionAsync(
                sessionId, null, deviceIdentifier, reason, cancellationToken);
            if (result.IsSuccess)
                ended++;
        }

        return ended;
    }

    public async Task<int> CleanupStaleSessionsAsync(TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - idleTimeout;

        var pendingStale = await _context.Sessions
            .Where(s => s.Status == SessionStatus.Pending && s.CreatedAtUtc < cutoff)
            .ToListAsync(cancellationToken);

        foreach (var session in pendingStale)
        {
            session.Status = SessionStatus.Ended;
            session.EndedAtUtc = now;
            session.EndReason = "Stale pending session auto-terminated";
            await _sessionService.AddSessionEventAsync(session.Id, SessionEventType.SessionEnded,
                "Session expired while pending", cancellationToken);
        }

        var activeStale = await _context.Sessions
            .Where(s => s.Status == SessionStatus.Active && s.StartedAtUtc.HasValue && s.StartedAtUtc.Value < cutoff)
            .ToListAsync(cancellationToken);

        foreach (var session in activeStale)
        {
            session.Status = SessionStatus.Ended;
            session.EndedAtUtc = now;
            session.EndReason = "Stale active session auto-terminated";
            await _sessionService.AddSessionEventAsync(session.Id, SessionEventType.SessionEnded,
                "Session timed out (idle)", cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);

        var count = pendingStale.Count + activeStale.Count;
        if (count > 0 && _logger is not null)
            _logger.LogInformation("Cleaned up {Count} stale sessions before cutoff {Cutoff}", count, cutoff);

        return count;
    }

    public async Task<int> CleanupPendingSessionsAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - staleAfter;

        var stale = await _context.Sessions
            .Where(s => s.Status == SessionStatus.Pending && s.CreatedAtUtc < cutoff)
            .ToListAsync(cancellationToken);

        foreach (var s in stale)
        {
            s.Status = SessionStatus.Ended;
            s.EndedAtUtc = now;
            s.EndReason = "Stale pending session auto-terminated";
            await _sessionService.AddSessionEventAsync(s.Id, SessionEventType.SessionEnded,
                "Session expired while pending", cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }

    public async Task<int> CleanupActiveSessionsAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - staleAfter;

        var stale = await _context.Sessions
            .Where(s => s.Status == SessionStatus.Active && s.StartedAtUtc.HasValue && s.StartedAtUtc.Value < cutoff)
            .ToListAsync(cancellationToken);

        foreach (var s in stale)
        {
            s.Status = SessionStatus.Ended;
            s.EndedAtUtc = now;
            s.EndReason = "Stale active session auto-terminated";
            await _sessionService.AddSessionEventAsync(s.Id, SessionEventType.SessionEnded,
                "Session timed out (idle)", cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }
}
