namespace RemoteSupport.Server.Application.Interfaces;

public interface ISessionEventNotifier
{
    Task NotifyConnectionRequestCreated(Guid agentUserId, string deviceIdentifier, Guid sessionId, string agentName, string? agentDisplayName, string agentRole, DateTime requestedAtUtc);
    Task NotifySessionAccepted(Guid agentUserId, string deviceIdentifier, Guid sessionId, string status, string? agentName, string? agentDisplayName, string? customerDeviceName);
    Task NotifySessionRejected(Guid agentUserId, string deviceIdentifier, Guid sessionId, string status, string? endReason);
    Task NotifySessionTerminated(Guid agentUserId, string deviceIdentifier, Guid sessionId, string status, string? endReason, DateTime? endedAtUtc);
    Task NotifyAgentError(Guid agentUserId, string message);
    Task NotifyCustomerError(string deviceIdentifier, string message);
}
