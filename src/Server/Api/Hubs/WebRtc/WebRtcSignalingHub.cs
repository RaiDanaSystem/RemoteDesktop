using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RemoteSupport.Server.Application.Interfaces;

namespace RemoteSupport.Server.Api.Hubs.WebRtc;

/// <summary>
/// Pure signaling relay hub for WebRTC SDP/ICE exchange between peers.
/// No server-side RTCPeerConnection — the server only validates and relays messages.
/// Supports multiple concurrent sessions with proper authorization.
/// </summary>
[Authorize]
public class WebRtcSignalingHub : Hub
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<WebRtcSignalingHub> _logger;

    private static readonly ConcurrentDictionary<string, PeerInfo> s_peers = new();
    private static readonly ConcurrentDictionary<string, HashSet<string>> s_sessionPeers = new();

    public WebRtcSignalingHub(
        ISessionManager sessionManager,
        ILogger<WebRtcSignalingHub> logger)
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

        var role = GetRole();
        var userId = GetUserId();
        var deviceIdentifier = GetDeviceIdentifier();

        if (role == "Customer" && !string.IsNullOrEmpty(deviceIdentifier))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"webrtc-customer:{deviceIdentifier}");
            _logger.LogDebug("WebRTC customer {Device} connected: {ConnectionId}", deviceIdentifier, Context.ConnectionId);
        }
        else if (userId.HasValue && (role == "SupportAgent" || role == "Admin"))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"webrtc-agent:{userId.Value}");
            _logger.LogDebug("WebRTC agent {UserId} connected: {ConnectionId}", userId.Value, Context.ConnectionId);
        }
        else
        {
            _logger.LogWarning("WebRTC connection rejected: invalid identity");
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Called by SupportAgent to signal it wants to start WebRTC with a session's customer.
    /// The server notifies the CustomerAgent to expect an incoming offer.
    /// </summary>
    public async Task RequestOffer(Guid sessionId)
    {
        var userId = GetUserId();
        var role = GetRole();

        if (role != "SupportAgent" && role != "Admin")
        {
            await Clients.Caller.SendAsync("Error", new { message = "Only support agents can initiate WebRTC connections." });
            return;
        }

        if (userId is null)
        {
            await Clients.Caller.SendAsync("Error", new { message = "Invalid user identity." });
            return;
        }

        var sessionStatus = await _sessionManager.GetSessionStatusAsync(sessionId, userId, null);
        if (!sessionStatus.SessionExists || sessionStatus.Status != "Active")
        {
            await Clients.Caller.SendAsync("Error", new { message = "Session not found or not active." });
            return;
        }

        RegisterPeer(Context.ConnectionId, sessionId.ToString(), "agent", userId?.ToString() ?? "unknown");

        await Clients.Caller.SendAsync("OfferRequested", new
        {
            sessionId,
            message = "Offer request acknowledged. Create your SDP offer and submit it."
        });

        _logger.LogInformation("WebRTC offer requested for session {SessionId} by agent {UserId}", sessionId, userId);
    }

    /// <summary>
    /// Called by SupportAgent after creating an SDP offer.
    /// Server relays the offer to the CustomerAgent for that session.
    /// </summary>
    public async Task SubmitOffer(Guid sessionId, string peerId, string sdpOffer)
    {
        var userId = GetUserId();
        var role = GetRole();

        if (role != "SupportAgent" && role != "Admin")
        {
            await Clients.Caller.SendAsync("Error", new { message = "Only support agents can submit offers." });
            return;
        }

        if (userId is null)
        {
            await Clients.Caller.SendAsync("Error", new { message = "Invalid user identity." });
            return;
        }

        var sessionStatus = await _sessionManager.GetSessionStatusAsync(sessionId, userId, null);
        if (!sessionStatus.SessionExists || sessionStatus.Status != "Active")
        {
            _logger.LogWarning("SubmitOffer rejected: session not found or not active. SessionId={SessionId}, Exists={Exists}, Status={Status}, DeviceName={DeviceName}",
                sessionId, sessionStatus.SessionExists, sessionStatus.Status, sessionStatus.CustomerDeviceName);
            await Clients.Caller.SendAsync("Error", new { message = "Session not found or not active." });
            return;
        }

        RegisterPeer(Context.ConnectionId, sessionId.ToString(), "agent", userId?.ToString() ?? "unknown");

        var customerGroup = GetCustomerGroup(sessionStatus);
        if (!string.IsNullOrEmpty(customerGroup))
        {
            try
            {
                await Clients.Group(customerGroup).SendAsync("OfferReceived", new
                {
                    peerId,
                    sessionId,
                    sdpOffer,
                    iceServers = GetIceServerInfo()
                });
                _logger.LogInformation("Relayed SDP offer to customer group {Group} for session {SessionId}",
                    customerGroup, sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send offer to group {GroupName}", customerGroup);
            }
        }
        else
        {
            _logger.LogWarning("SubmitOffer: customer signaling group is empty for session {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Called by CustomerAgent after creating an SDP answer.
    /// Server relays the answer to the SupportAgent for that session.
    /// </summary>
    public async Task SubmitAnswer(Guid sessionId, string peerId, string sdpAnswer)
    {
        var role = GetRole();
        var deviceIdentifier = GetDeviceIdentifier();

        if (role != "Customer")
        {
            await Clients.Caller.SendAsync("Error", new { message = "Only customers can submit answers." });
            return;
        }

        if (string.IsNullOrEmpty(deviceIdentifier))
        {
            await Clients.Caller.SendAsync("Error", new { message = "Invalid device identity." });
            return;
        }

        var sessionStatus = await _sessionManager.GetSessionStatusAsync(sessionId, null, deviceIdentifier);
        if (!sessionStatus.SessionExists || sessionStatus.Status != "Active")
        {
            await Clients.Caller.SendAsync("Error", new { message = "Session not found or not active." });
            return;
        }

        RegisterPeer(Context.ConnectionId, sessionId.ToString(), "customer", deviceIdentifier!);

        if (sessionStatus.AgentUserId.HasValue)
        {
            var agentId = sessionStatus.AgentUserId.Value;
            await Clients.Group($"webrtc-agent:{agentId}")
                .SendAsync("AnswerReceived", new
                {
                    peerId,
                    sessionId,
                    sdpAnswer
                });

            _logger.LogInformation("Relayed SDP answer to agent {AgentId} for session {SessionId}", agentId, sessionId);
        }
    }

    /// <summary>
    /// Relays an ICE candidate from one peer to the other in the same session.
    /// </summary>
    public async Task SubmitIceCandidate(
        Guid sessionId, string peerId, string candidate, string sdpMid, int sdpMLineIndex)
    {
        try
        {
            var role = GetRole();
            var userId = GetUserId();
            var deviceIdentifier = GetDeviceIdentifier();
            _logger.LogDebug("SubmitIceCandidate called: session={SessionId}, peer={PeerId}, role={Role}, user={UserId}, device={Device}",
                sessionId, peerId, role, userId, deviceIdentifier);

            var sessionStatus = await ValidateSessionAccess(sessionId, role);
            if (sessionStatus is null)
            {
                _logger.LogWarning("SubmitIceCandidate rejected: session validation failed for session {SessionId}", sessionId);
                return;
            }

            RegisterPeer(Context.ConnectionId, sessionId.ToString(), role ?? "unknown",
                userId?.ToString() ?? deviceIdentifier ?? "");

            _logger.LogDebug("SubmitIceCandidate: session={SessionId}, status={Status}, agentUserId={AgentUserId}, customerDeviceName={CustomerDeviceName}",
                sessionId, sessionStatus.Status, sessionStatus.AgentUserId, sessionStatus.CustomerDeviceName);

            if (role == "Customer" && sessionStatus.AgentUserId.HasValue)
            {
                var agentId = sessionStatus.AgentUserId.Value;
                await Clients.Group($"webrtc-agent:{agentId}")
                    .SendAsync("IceCandidateReceived", new
                    {
                        peerId,
                        sessionId,
                        candidate,
                        sdpMid,
                        sdpMLineIndex
                    });

                _logger.LogInformation("Relayed ICE candidate from customer to agent {AgentId} for session {SessionId}",
                    agentId, sessionId);
            }
            else if (role is "SupportAgent" or "Admin" && GetCustomerGroup(sessionStatus) is { Length: > 0 } iceGroup)
            {
                await Clients.Group(iceGroup)
                    .SendAsync("IceCandidateReceived", new
                    {
                        peerId,
                        sessionId,
                        candidate,
                        sdpMid,
                        sdpMLineIndex
                    });

                _logger.LogInformation("Relayed ICE candidate from agent to customer group {Group} for session {SessionId}",
                    iceGroup, sessionId);
            }
            else
            {
                _logger.LogWarning("SubmitIceCandidate: no valid relay path. role={Role}, hasAgent={HasAgent}, customerDevice={CustomerDevice}",
                    role, sessionStatus.AgentUserId.HasValue, sessionStatus.CustomerDeviceName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SubmitIceCandidate: session={SessionId}, peer={PeerId}", sessionId, peerId);
            throw;
        }
    }

    /// <summary>
    /// Relays a connection close signal to the other peer.
    /// </summary>
    public async Task CloseConnection(Guid sessionId, string peerId)
    {
        var role = GetRole();
        var sessionStatus = await ValidateSessionAccess(sessionId, role);
        if (sessionStatus is null) return;

        if (role == "Customer" && sessionStatus.AgentUserId.HasValue)
        {
            var agentId = sessionStatus.AgentUserId.Value;
            await Clients.Group($"webrtc-agent:{agentId}")
                .SendAsync("PeerClosed", new { peerId, sessionId, reason = "remote_disconnect" });
        }
        else if (role is "SupportAgent" or "Admin" && GetCustomerGroup(sessionStatus) is { Length: > 0 } closeGroup)
        {
            await Clients.Group(closeGroup)
                .SendAsync("PeerClosed", new { peerId, sessionId, reason = "remote_disconnect" });
        }

        RemovePeer(Context.ConnectionId);
        _logger.LogInformation("WebRTC connection closed for session {SessionId}, peer {PeerId}", sessionId, peerId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var removed = RemovePeer(Context.ConnectionId);
        if (removed != null)
        {
            bool shouldTerminate = false;
            string? sessionId = null;
            lock (s_sessionPeers)
            {
                if (s_sessionPeers.TryGetValue(removed.SessionId, out var remaining))
                {
                    if (remaining.Count == 0)
                    {
                        shouldTerminate = true;
                        sessionId = removed.SessionId;
                        s_sessionPeers.TryRemove(sessionId, out _);
                    }
                }
            }

            if (shouldTerminate && sessionId != null)
            {
                _logger.LogInformation(
                    "Last WebRTC peer disconnected for session {SessionId} — terminating stale session", sessionId);
                try
                {
                    var result = await _sessionManager.TerminateSessionAsync(
                        Guid.Parse(sessionId), null, null, "WebRTC signaling last peer disconnected",
                        CancellationToken.None);
                    if (result.IsSuccess)
                        _logger.LogInformation("Session {SessionId} terminated after last peer disconnect", sessionId);
                    else
                        _logger.LogWarning("Failed to terminate session {SessionId}: {Error}", sessionId, result.ErrorMessage);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error terminating stale session {SessionId} on peer disconnect", sessionId);
                }
            }
        }
        _logger.LogDebug("WebRTC signaling client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private async Task<SessionStatusResult?> ValidateSessionAccess(Guid sessionId, string? role, CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        var deviceIdentifier = GetDeviceIdentifier();

        SessionStatusResult sessionStatus;
        if (role == "Customer")
        {
            sessionStatus = await _sessionManager.GetSessionStatusAsync(sessionId, null, deviceIdentifier, cancellationToken);
        }
        else
        {
            sessionStatus = await _sessionManager.GetSessionStatusAsync(sessionId, userId, null, cancellationToken);
        }

        if (!sessionStatus.SessionExists || sessionStatus.Status != "Active")
        {
            await Clients.Caller.SendAsync("Error", new { message = "Session not found or not active." });
            return null;
        }

        return sessionStatus;
    }

    private void RegisterPeer(string connectionId, string sessionId, string role, string identity)
    {
        var info = new PeerInfo(connectionId, sessionId, role, identity);
        s_peers[connectionId] = info;

        s_sessionPeers.AddOrUpdate(sessionId,
            _ => new HashSet<string> { connectionId },
            (_, set) => { lock (set) { set.Add(connectionId); } return set; });
    }

    private PeerInfo? RemovePeer(string connectionId)
    {
        if (s_peers.TryRemove(connectionId, out var info))
        {
            if (s_sessionPeers.TryGetValue(info.SessionId, out var set))
            {
                lock (set)
                {
                    set.Remove(connectionId);
                    if (set.Count == 0)
                        s_sessionPeers.TryRemove(info.SessionId, out _);
                }
            }
            return info;
        }
        return null;
    }

    private static string? GetCustomerGroup(SessionStatusResult sessionStatus)
    {
        var key = !string.IsNullOrEmpty(sessionStatus.CustomerDeviceIdentifier)
            ? sessionStatus.CustomerDeviceIdentifier
            : sessionStatus.CustomerDeviceName;
        return string.IsNullOrEmpty(key) ? null : $"webrtc-customer:{key}";
    }

    private static object GetIceServerInfo()
    {
        return new[]
        {
            new { urls = new[] { "stun:stun.l.google.com:19302" } }
        };
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

    private record PeerInfo(string ConnectionId, string SessionId, string Role, string Identity);
}
