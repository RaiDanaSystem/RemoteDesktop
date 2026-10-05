using Microsoft.AspNetCore.SignalR;

namespace RemoteSupport.Server.Api.Hubs;

public class SessionHubContext : ISessionHubContext
{
    private readonly IHubContext<SessionHub> _hubContext;

    public SessionHubContext(IHubContext<SessionHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyAgentConnectionRequest(Guid agentUserId, ConnectionRequestCreatedEvent sessionEvent)
    {
        await _hubContext.Clients.Group($"agent:{agentUserId}")
            .SendAsync(SessionEvents.ConnectionRequestCreated, sessionEvent);
    }

    public async Task NotifyCustomerConnectionRequest(string deviceIdentifier, ConnectionRequestCreatedEvent sessionEvent)
    {
        await _hubContext.Clients.Group($"customer:{deviceIdentifier}")
            .SendAsync(SessionEvents.ConnectionRequestCreated, sessionEvent);
    }

    public async Task NotifySessionAccepted(Guid agentUserId, SessionStateEvent sessionEvent)
    {
        await _hubContext.Clients.Group($"agent:{agentUserId}")
            .SendAsync(SessionEvents.ConnectionRequestAccepted, sessionEvent);
    }

    public async Task NotifySessionRejected(Guid agentUserId, SessionStateEvent sessionEvent)
    {
        await _hubContext.Clients.Group($"agent:{agentUserId}")
            .SendAsync(SessionEvents.ConnectionRequestRejected, sessionEvent);
    }

    public async Task NotifySessionStarted(Guid agentUserId, SessionStateEvent sessionEvent)
    {
        await _hubContext.Clients.Group($"agent:{agentUserId}")
            .SendAsync(SessionEvents.SessionStarted, sessionEvent);
    }

    public async Task NotifySessionTerminated(Guid agentUserId, SessionStateEvent sessionEvent)
    {
        await _hubContext.Clients.Group($"agent:{agentUserId}")
            .SendAsync(SessionEvents.SessionTerminated, sessionEvent);
    }

    public async Task NotifyCustomerSessionTerminated(string deviceIdentifier, SessionStateEvent sessionEvent)
    {
        await _hubContext.Clients.Group($"customer:{deviceIdentifier}")
            .SendAsync(SessionEvents.SessionTerminated, sessionEvent);
    }

    public async Task NotifyAgentError(Guid agentUserId, ConnectionErrorEvent errorEvent)
    {
        await _hubContext.Clients.Group($"agent:{agentUserId}")
            .SendAsync(SessionEvents.ConnectionError, errorEvent);
    }

    public async Task NotifyCustomerError(string deviceIdentifier, ConnectionErrorEvent errorEvent)
    {
        await _hubContext.Clients.Group($"customer:{deviceIdentifier}")
            .SendAsync(SessionEvents.ConnectionError, errorEvent);
    }
}
