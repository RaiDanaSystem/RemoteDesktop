using System.Collections.Concurrent;
using SIPSorcery.Net;

namespace RemoteSupport.Server.Api.Transport.WebRtc;

public class WebRtcPeerManager
{
    private readonly ConcurrentDictionary<string, WebRtcPeer> _peers = new();
    private readonly ILogger<WebRtcPeerManager> _logger;

    public WebRtcPeerManager(ILogger<WebRtcPeerManager> logger)
    {
        _logger = logger;
    }

    public string RegisterPeer(RTCPeerConnection peerConnection, string sessionId, string role)
    {
        var id = Guid.NewGuid().ToString("N");
        var peer = new WebRtcPeer(id, peerConnection, sessionId, role);
        _peers[id] = peer;
        _logger.LogDebug("Registered WebRTC peer {Id} for session {SessionId} ({Role})", id, sessionId, role);
        return id;
    }

    public WebRtcPeer? GetPeer(string peerId)
    {
        _peers.TryGetValue(peerId, out var peer);
        return peer;
    }

    public void RemovePeer(string peerId)
    {
        if (_peers.TryRemove(peerId, out var peer))
        {
            try { peer.PeerConnection?.Close("removed"); } catch { }
            _logger.LogDebug("Removed WebRTC peer {PeerId}", peerId);
        }
    }

    public IReadOnlyList<WebRtcPeer> GetPeersForSession(string sessionId)
    {
        return _peers.Values.Where(p => p.SessionId == sessionId).ToList();
    }
}

public record WebRtcPeer(
    string Id,
    RTCPeerConnection PeerConnection,
    string SessionId,
    string Role);
