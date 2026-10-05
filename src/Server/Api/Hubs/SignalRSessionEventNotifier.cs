using RemoteSupport.Server.Application.Interfaces;

namespace RemoteSupport.Server.Api.Hubs;

public class SignalRSessionEventNotifier : ISessionEventNotifier
{
    private readonly ISessionHubContext _hubContext;

    public SignalRSessionEventNotifier(ISessionHubContext hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyConnectionRequestCreated(Guid agentUserId, string deviceIdentifier, Guid sessionId, string agentName, string? agentDisplayName, string agentRole, DateTime requestedAtUtc)
    {
        var evt = new ConnectionRequestCreatedEvent(sessionId, agentName, agentDisplayName, agentRole, requestedAtUtc);
        await _hubContext.NotifyCustomerConnectionRequest(deviceIdentifier, evt);
    }

    public async Task NotifySessionAccepted(Guid agentUserId, string deviceIdentifier, Guid sessionId, string status, string? agentName, string? agentDisplayName, string? customerDeviceName)
    {
        var evt = new SessionStateEvent(sessionId, status, agentName, agentDisplayName, customerDeviceName, DateTime.UtcNow, null, null);
        await _hubContext.NotifySessionAccepted(agentUserId, evt);
    }

    public async Task NotifySessionRejected(Guid agentUserId, string deviceIdentifier, Guid sessionId, string status, string? endReason)
    {
        var evt = new SessionStateEvent(sessionId, status, null, null, null, null, DateTime.UtcNow, endReason);
        await _hubContext.NotifySessionRejected(agentUserId, evt);
    }

    public async Task NotifySessionTerminated(Guid agentUserId, string deviceIdentifier, Guid sessionId, string status, string? endReason, DateTime? endedAtUtc)
    {
        var evt = new SessionStateEvent(sessionId, status, null, null, null, null, endedAtUtc ?? DateTime.UtcNow, endReason);
        await _hubContext.NotifySessionTerminated(agentUserId, evt);
        await _hubContext.NotifyCustomerSessionTerminated(deviceIdentifier, evt);
    }

    public async Task NotifyAgentError(Guid agentUserId, string message)
    {
        await _hubContext.NotifyAgentError(agentUserId, new ConnectionErrorEvent(message));
    }

    public async Task NotifyCustomerError(string deviceIdentifier, string message)
    {
        await _hubContext.NotifyCustomerError(deviceIdentifier, new ConnectionErrorEvent(message));
    }
}
