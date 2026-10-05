namespace RemoteSupport.Server.Api.Hubs;

public interface ISessionHubContext
{
    Task NotifyAgentConnectionRequest(Guid agentUserId, ConnectionRequestCreatedEvent sessionEvent);
    Task NotifyCustomerConnectionRequest(string deviceIdentifier, ConnectionRequestCreatedEvent sessionEvent);
    Task NotifySessionAccepted(Guid agentUserId, SessionStateEvent sessionEvent);
    Task NotifySessionRejected(Guid agentUserId, SessionStateEvent sessionEvent);
    Task NotifySessionStarted(Guid agentUserId, SessionStateEvent sessionEvent);
    Task NotifySessionTerminated(Guid agentUserId, SessionStateEvent sessionEvent);
    Task NotifyCustomerSessionTerminated(string deviceIdentifier, SessionStateEvent sessionEvent);
    Task NotifyAgentError(Guid agentUserId, ConnectionErrorEvent errorEvent);
    Task NotifyCustomerError(string deviceIdentifier, ConnectionErrorEvent errorEvent);
}
