using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace RemoteSupport.Server.Api.Hubs;

public class CustomerAgentUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        var role = connection.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (role == "Customer")
        {
            return connection.User?.FindFirst("device_identifier")?.Value;
        }
        return connection.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
