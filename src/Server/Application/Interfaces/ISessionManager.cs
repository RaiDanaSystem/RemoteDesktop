using RemoteSupport.Server.Domain.Entities;

namespace RemoteSupport.Server.Application.Interfaces;

public interface ISessionManager
{
    Task<SessionInitiationResult> InitiateConnectionAsync(Guid agentUserId, string supportCode, CancellationToken cancellationToken = default);
    Task<ConnectionRequestPollResult> GetPendingConnectionRequestAsync(string deviceIdentifier, CancellationToken cancellationToken = default);
    Task<SessionAcceptResult> AcceptConnectionAsync(Guid sessionId, string customerDeviceIdentifier, CancellationToken cancellationToken = default);
    Task<SessionRejectResult> RejectConnectionAsync(Guid sessionId, string customerDeviceIdentifier, CancellationToken cancellationToken = default);
    Task<SessionStatusResult> GetSessionStatusAsync(Guid sessionId, Guid? userId, string? deviceIdentifier, CancellationToken cancellationToken = default);
    Task<TerminationResult> TerminateSessionAsync(Guid sessionId, Guid? userId, string? deviceIdentifier, string? reason, CancellationToken cancellationToken = default);
    Task<int> TerminateOpenSessionsForDeviceAsync(string deviceIdentifier, string reason, CancellationToken cancellationToken = default);
    Task<int> CleanupStaleSessionsAsync(TimeSpan idleTimeout, CancellationToken cancellationToken = default);
    Task<int> CleanupPendingSessionsAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default);
    Task<int> CleanupActiveSessionsAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default);
}

public class SessionInitiationResult
{
    public bool IsSuccess { get; set; }
    public Guid? SessionId { get; set; }
    public string? ErrorMessage { get; set; }
    public string? CustomerDeviceName { get; set; }
    public string? CustomerOperatingSystem { get; set; }
}

public class ConnectionRequestPollResult
{
    public bool HasPendingRequest { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? AgentUserId { get; set; }
    public string? AgentName { get; set; }
    public string? AgentDisplayName { get; set; }
    public string? AgentRole { get; set; }
    public DateTime? RequestedAtUtc { get; set; }
}

public class SessionAcceptResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}

public class SessionRejectResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}

public class SessionStatusResult
{
    public bool SessionExists { get; set; }
    public string? Status { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? AgentUserId { get; set; }
    public string? AgentName { get; set; }
    public string? AgentDisplayName { get; set; }
    public string? CustomerDeviceName { get; set; }
    public string? CustomerDeviceIdentifier { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string? EndReason { get; set; }
}

public class TerminationResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}
