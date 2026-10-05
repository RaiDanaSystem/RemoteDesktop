using SIPSorcery.Net;

namespace RemoteSupport.Server.Api.Transport.WebRtc;

public interface IWebRtcTransportService
{
    Task<WebRtcOfferResult> CreateOfferAsync(string sessionId, string role, CancellationToken cancellationToken = default);
    Task<SetAnswerResult> SetAnswerAsync(string peerId, string sdpAnswer, CancellationToken cancellationToken = default);
    Task AddIceCandidateAsync(string peerId, string candidate, string sdpMid, int sdpMLineIndex, CancellationToken cancellationToken = default);
    Task ClosePeerAsync(string peerId, string reason = "normal", CancellationToken cancellationToken = default);
    WebRtcDiagnostics GetDiagnostics(string peerId);
}

public class WebRtcTransportService : IWebRtcTransportService
{
    private readonly WebRtcPeerManager _peerManager;
    private readonly ILogger<WebRtcTransportService> _logger;

    public WebRtcTransportService(WebRtcPeerManager peerManager, ILogger<WebRtcTransportService> logger)
    {
        _peerManager = peerManager;
        _logger = logger;
    }

    public async Task<WebRtcOfferResult> CreateOfferAsync(string sessionId, string role, CancellationToken cancellationToken = default)
    {
        var iceServers = GetDefaultIceServers();
        var config = new RTCConfiguration { iceServers = new List<RTCIceServer>(iceServers) };
        var pc = new RTCPeerConnection(config);

        var peerId = _peerManager.RegisterPeer(pc, sessionId, role);

        var dataChannel = await pc.createDataChannel("data", null);

        var offer = pc.createOffer(new RTCOfferOptions
        {
            X_WaitForIceGatheringToComplete = false
        });

        await pc.setLocalDescription(offer);

        _logger.LogInformation("Created WebRTC offer for peer {PeerId}, session {SessionId}", peerId, sessionId);

        return new WebRtcOfferResult
        {
            PeerId = peerId,
            SdpOffer = offer.sdp,
            IceServers = iceServers?.Select(s => new IceServerInfo(s.urls, s.username, s.credential)).ToArray()
        };
    }

    public async Task<SetAnswerResult> SetAnswerAsync(string peerId, string sdpAnswer, CancellationToken cancellationToken = default)
    {
        var peer = _peerManager.GetPeer(peerId);
        if (peer is null)
        {
            return new SetAnswerResult { IsSuccess = false, ErrorMessage = "Peer not found." };
        }

        var answerInit = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = sdpAnswer
        };

        var result = peer.PeerConnection.setRemoteDescription(answerInit);

        if (result != SetDescriptionResultEnum.OK)
        {
            return new SetAnswerResult { IsSuccess = false, ErrorMessage = $"Failed to set answer: {result}" };
        }

        _logger.LogInformation("Set SDP answer for peer {PeerId}", peerId);
        return new SetAnswerResult { IsSuccess = true };
    }

    public async Task AddIceCandidateAsync(string peerId, string candidate, string sdpMid, int sdpMLineIndex, CancellationToken cancellationToken = default)
    {
        var peer = _peerManager.GetPeer(peerId);
        if (peer is null) return;

        var iceCandidate = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid,
            sdpMLineIndex = (ushort)sdpMLineIndex
        };

        peer.PeerConnection.addIceCandidate(iceCandidate);
    }

    public async Task ClosePeerAsync(string peerId, string reason = "normal", CancellationToken cancellationToken = default)
    {
        var peer = _peerManager.GetPeer(peerId);
        if (peer is not null)
        {
            try
            {
                peer.PeerConnection.Close(reason);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing peer {PeerId}", peerId);
            }
            _peerManager.RemovePeer(peerId);
        }
    }

    public WebRtcDiagnostics GetDiagnostics(string peerId)
    {
        var peer = _peerManager.GetPeer(peerId);
        if (peer is null)
        {
            return new WebRtcDiagnostics { ConnectionState = "NotFound" };
        }

        var pc = peer.PeerConnection;
        return new WebRtcDiagnostics
        {
            PeerId = peerId,
            SessionId = peer.SessionId,
            Role = peer.Role,
            ConnectionState = pc.connectionState.ToString(),
            IceConnectionState = pc.iceConnectionState.ToString(),
            IceGatheringState = pc.iceGatheringState.ToString(),
            SignalingState = pc.signalingState.ToString(),
            ConnectionType = DetermineConnectionType(pc),
            ConnectedAtUtc = pc.connectionState == RTCPeerConnectionState.connected ? DateTime.UtcNow : null
        };
    }

    private static RTCIceServer[] GetDefaultIceServers()
    {
        return new[]
        {
            new RTCIceServer
            {
                urls = "stun:stun.l.google.com:19302"
            }
        };
    }

    public static RTCConfiguration GetConfigurationWithTurn(string turnUrl, string username, string credential)
    {
        return new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer
                {
                    urls = "stun:stun.l.google.com:19302"
                },
                new RTCIceServer
                {
                    urls = turnUrl,
                    username = username,
                    credential = credential
                }
            }
        };
    }

    public static RTCConfiguration GetRelayOnlyConfiguration(string turnUrl, string username, string credential)
    {
        return new RTCConfiguration
        {
            iceTransportPolicy = RTCIceTransportPolicy.relay,
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer
                {
                    urls = turnUrl,
                    username = username,
                    credential = credential
                }
            }
        };
    }

    private static string DetermineConnectionType(RTCPeerConnection pc)
    {
        if (pc.iceConnectionState == RTCIceConnectionState.connected)
        {
            return "Connected";
        }
        return pc.iceConnectionState.ToString();
    }
}

public record WebRtcOfferResult
{
    public string PeerId { get; init; } = string.Empty;
    public string SdpOffer { get; init; } = string.Empty;
    public IceServerInfo[]? IceServers { get; init; }
}

public record IceServerInfo(string Urls, string? Username, string? Credential);

public record SetAnswerResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
}

public class WebRtcDiagnostics
{
    public string PeerId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string ConnectionState { get; set; } = "New";
    public string IceConnectionState { get; set; } = "New";
    public string IceGatheringState { get; set; } = "New";
    public string SignalingState { get; set; } = "Stable";
    public string ConnectionType { get; set; } = "None";
    public DateTime? ConnectedAtUtc { get; set; }
}
