using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RemoteSupport.Server.Application.Interfaces;

namespace RemoteSupport.Server.Api.Hubs;

[Authorize]
public class SessionHub : Hub
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SessionHub> _logger;

    public SessionHub(ISessionManager sessionManager, ILogger<SessionHub> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var identity = Context.User?.Identity;
        if (identity is null || !identity.IsAuthenticated)
        {
            Context.Abort();
            return;
        }

        var userId = GetUserId();
        var role = GetRole();
        var deviceIdentifier = GetDeviceIdentifier();

        if (role == "Customer" && !string.IsNullOrEmpty(deviceIdentifier))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"customer:{deviceIdentifier}");
            _logger.LogDebug("Customer agent {Device} connected: {ConnectionId}", deviceIdentifier, Context.ConnectionId);
        }
        else if (userId.HasValue && (role == "SupportAgent" || role == "Admin"))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"agent:{userId.Value}");
            _logger.LogDebug("Support agent {UserId} connected: {ConnectionId}", userId.Value, Context.ConnectionId);
        }
        else
        {
            _logger.LogWarning("Connection rejected: invalid identity");
            Context.Abort();
            return;
        }

        await SendHeartbeat();
        await base.OnConnectedAsync();
    }

    public async Task SendHeartbeat()
    {
        await Clients.Caller.SendAsync(SessionEvents.Heartbeat, new HeartbeatEvent(
            DateTime.UtcNow,
            Context.ConnectionId));
    }

    public async Task JoinSessionGroup(Guid sessionId)
    {
        var userId = GetUserId();
        var role = GetRole();
        var deviceIdentifier = GetDeviceIdentifier();

        var session = await _sessionManager.GetSessionStatusAsync(sessionId, userId, deviceIdentifier);
        if (!session.SessionExists)
        {
            await Clients.Caller.SendAsync(SessionEvents.ConnectionError,
                new ConnectionErrorEvent("Session not found."));
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"session:{sessionId}");
        _logger.LogDebug("User joined session group {SessionId}", sessionId);
    }

    public async Task LeaveSessionGroup(Guid sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session:{sessionId}");
    }

    public async Task RequestSessionStatus(Guid sessionId)
    {
        var userId = GetUserId();
        var role = GetRole();
        var deviceIdentifier = GetDeviceIdentifier();

        var session = await _sessionManager.GetSessionStatusAsync(sessionId, userId, deviceIdentifier);
        if (!session.SessionExists)
        {
            await Clients.Caller.SendAsync(SessionEvents.ConnectionError,
                new ConnectionErrorEvent("Session not found."));
            return;
        }

        var stateEvent = new SessionStateEvent(
            session.SessionId!.Value,
            session.Status!,
            session.AgentName,
            session.AgentDisplayName,
            session.CustomerDeviceName,
            session.StartedAtUtc,
            session.EndedAtUtc,
            session.EndReason);

        await Clients.Caller.SendAsync(SessionEvents.SessionStatusChanged, stateEvent);
    }

    private Guid? GetUserId()
    {
        var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdStr, out var userId) ? userId : null;
    }

    private string? GetRole()
    {
        return Context.User?.FindFirst(ClaimTypes.Role)?.Value;
    }

    private string? GetDeviceIdentifier()
    {
        return Context.User?.FindFirst("device_identifier")?.Value;
    }
}
