namespace RemoteSupport.Shared.Transport;

/// <summary>
/// Client-side signaling interface for WebRTC SDP/ICE exchange via SignalR.
/// Both CustomerAgent and SupportAgent implement this to send signaling messages.
/// </summary>
public interface IWebRtcSignalingClient : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(string serverUrl, string accessToken, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task RequestOfferAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task SubmitOfferAsync(Guid sessionId, string peerId, string sdpOffer, CancellationToken cancellationToken = default);
    Task SendAnswerAsync(Guid sessionId, string peerId, string sdpAnswer, CancellationToken cancellationToken = default);
    Task SendIceCandidateAsync(Guid sessionId, string peerId, string candidate, string sdpMid, int sdpMLineIndex, CancellationToken cancellationToken = default);
    Task ClosePeerConnectionAsync(Guid sessionId, string peerId, CancellationToken cancellationToken = default);

    event EventHandler<OfferReceivedEventArgs>? OfferReceived;
    event EventHandler<AnswerReceivedEventArgs>? AnswerReceived;
    event EventHandler<IceCandidateReceivedEventArgs>? IceCandidateReceived;
    event EventHandler<PeerClosedEventArgs>? PeerClosed;
    event EventHandler<string>? Error;
    event EventHandler? Connected;
    event EventHandler? Disconnected;
}

public sealed class OfferReceivedEventArgs : EventArgs
{
    public Guid SessionId { get; init; }
    public string PeerId { get; init; } = string.Empty;
    public string SdpOffer { get; init; } = string.Empty;
    public IceServerConfig[]? IceServers { get; init; }
}

public sealed class AnswerReceivedEventArgs : EventArgs
{
    public Guid SessionId { get; init; }
    public string PeerId { get; init; } = string.Empty;
    public string SdpAnswer { get; init; } = string.Empty;
}

public sealed class IceCandidateReceivedEventArgs : EventArgs
{
    public string PeerId { get; init; } = string.Empty;
    public string Candidate { get; init; } = string.Empty;
    public string SdpMid { get; init; } = string.Empty;
    public int SdpMLineIndex { get; init; }
}

public sealed class PeerClosedEventArgs : EventArgs
{
    public string PeerId { get; init; } = string.Empty;
    public string? Reason { get; init; }
}
