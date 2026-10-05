using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace RemoteSupport.Server.Api.Hubs;

public class SupportAgentUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        return connection.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
