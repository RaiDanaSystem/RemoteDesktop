namespace RemoteSupport.Server.Api.Hubs;

public static class SessionEvents
{
    public const string ConnectionRequestCreated = "ConnectionRequestCreated";
    public const string ConnectionRequestAccepted = "ConnectionRequestAccepted";
    public const string ConnectionRequestRejected = "ConnectionRequestRejected";
    public const string SessionStarted = "SessionStarted";
    public const string SessionTerminated = "SessionTerminated";
    public const string SessionStatusChanged = "SessionStatusChanged";
    public const string ConnectionError = "ConnectionError";
    public const string Heartbeat = "Heartbeat";
}

public record ConnectionRequestCreatedEvent(
    Guid SessionId,
    string AgentName,
    string? AgentDisplayName,
    string AgentRole,
    DateTime RequestedAtUtc);

public record SessionStateEvent(
    Guid SessionId,
    string Status,
    string? AgentName,
    string? AgentDisplayName,
    string? CustomerDeviceName,
    DateTime? StartedAtUtc,
    DateTime? EndedAtUtc,
    string? EndReason);

public record ConnectionErrorEvent(
    string Message,
    string? Details = null);

public record HeartbeatEvent(
    DateTime ServerTimeUtc,
    string? ConnectionId);
