using System.Text.Json;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace RemoteSupport.Shared.Transport.WebRtc;

/// <summary>
/// Orchestrates the WebRTC connection lifecycle for a session.
/// Handles signaling exchange, peer creation, and data channel management.
/// This is the primary entry point for both CustomerAgent and SupportAgent.
/// </summary>
public sealed class WebRtcSessionManager : IAsyncDisposable
{
    private readonly SignalingClient _signalingClient;
    private readonly WebRtcConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WebRtcSessionManager> _logger;

    private WebRtcPeer? _peer;
    private Guid _sessionId;
    private bool _isInitiator;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private bool _disposed;

    public DataChannelState State => _peer?.State ?? DataChannelState.New;
    public string? PeerId => _peer?.PeerId;
    public bool IsConnected => _peer?.State == DataChannelState.Connected;
    public IDataChannel? Channel => _peer;
    public bool IsSignalingConnected => _signalingClient.IsConnected;

    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<Exception>? Error;
    public event EventHandler<string>? LogMessage;
    public event EventHandler? SignalingConnected;
    public event EventHandler? SignalingDisconnected;

    public WebRtcSessionManager(
        SignalingClient signalingClient,
        WebRtcConfiguration config,
        ILoggerFactory loggerFactory)
    {
        _signalingClient = signalingClient;
        _config = config;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WebRtcSessionManager>();

        _signalingClient.OfferReceived += OnOfferReceived;
        _signalingClient.AnswerReceived += OnAnswerReceived;
        _signalingClient.IceCandidateReceived += OnIceCandidateReceived;
        _signalingClient.PeerClosed += OnPeerClosed;
        _signalingClient.Error += OnSignalingError;
        _signalingClient.Connected += (_, _) =>
        {
            _logger.LogInformation("Signaling connected");
            SignalingConnected?.Invoke(this, EventArgs.Empty);
        };
        _signalingClient.Disconnected += (_, _) =>
        {
            _logger.LogWarning("Signaling disconnected");
            SignalingDisconnected?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>
    /// Connects the SignalR signaling client to the server's WebRTC signaling hub.
    /// Must be called before any WebRTC negotiation.
    /// </summary>
    public async Task ConnectSignalingAsync(string serverUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        if (_signalingClient.IsConnected)
        {
            _logger.LogDebug("Signaling already connected");
            return;
        }

        _logger.LogInformation("Connecting signaling to {ServerUrl}", serverUrl);
        await _signalingClient.ConnectAsync(serverUrl, accessToken, cancellationToken);
    }

    /// <summary>
    /// Initiates a WebRTC connection as the offerer (used by SupportAgent).
    /// Creates the peer, generates an SDP offer, and submits it to the server.
    /// </summary>
    public async Task ConnectAsOffererAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            _sessionId = sessionId;
            _isInitiator = true;

            await ResetPeerAsync();
            _peer = new WebRtcPeer(
                Guid.NewGuid().ToString("N"),
                _config,
                _loggerFactory.CreateLogger<WebRtcPeer>());

            WirePeerEvents(_peer);
            _peer.OnLocalIceCandidate += OnLocalIceCandidate;

            var sdpOffer = await _peer.CreateOfferAsync(sessionId.ToString(), cancellationToken);

            await _signalingClient.RequestOfferAsync(sessionId, cancellationToken);
            await _signalingClient.SubmitOfferAsync(sessionId, _peer.PeerId, sdpOffer, cancellationToken);

            LogMessage?.Invoke(this, "SDP offer created and submitted to server");
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Prepares to receive a WebRTC connection as the answerer (used by CustomerAgent).
    /// Does not create a peer yet — waits for the offer.
    /// </summary>
    public async Task PrepareAsAnswererAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await ResetPeerAsync();
        _sessionId = sessionId;
        _isInitiator = false;
        _logger.LogInformation("Prepared as answerer for session {SessionId}", sessionId);
    }

    /// <summary>
    /// Closes the current WebRTC peer without disposing signaling, so a new session can reuse this manager.
    /// </summary>
    public async Task ResetPeerAsync()
    {
        var peer = _peer;
        _peer = null;
        if (peer is null) return;

        try
        {
            if (peer.PeerId is not null && _sessionId != Guid.Empty && _signalingClient.IsConnected)
            {
                try
                {
                    await _signalingClient.ClosePeerConnectionAsync(_sessionId, peer.PeerId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ClosePeerConnection during reset");
                }
            }

            await peer.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error resetting WebRTC peer");
        }
    }

    /// <summary>
    /// Sends a ping message over the data channel.
    /// </summary>
    public Task SendPingAsync(CancellationToken cancellationToken = default)
    {
        if (_peer is null || _peer.State != DataChannelState.Connected)
        {
            _logger.LogWarning("Cannot send ping: not connected");
            return Task.CompletedTask;
        }

        var ping = new PingMessage();
        var payload = JsonSerializer.SerializeToUtf8Bytes(ping);
        var envelope = new TransportEnvelope
        {
            MessageType = TransportMessageType.Ping,
            Payload = payload
        };

        return _peer.SendAsync(envelope.Serialize(), cancellationToken);
    }

    /// <summary>
    /// Sends raw data over the data channel with a transport envelope.
    /// </summary>
    public Task SendAsync(TransportMessageType messageType, byte[] payload, CancellationToken cancellationToken = default)
    {
        if (_peer is null || _peer.State != DataChannelState.Connected)
        {
            _logger.LogWarning("Cannot send: not connected (type={Type})", messageType);
            return Task.CompletedTask;
        }

        var envelope = new TransportEnvelope
        {
            MessageType = messageType,
            Payload = payload
        };

        return _peer.SendAsync(envelope.Serialize(), cancellationToken);
    }

    /// <summary>
    /// Event fired when a raw transport envelope is received.
    /// </summary>
    public event EventHandler<TransportEnvelope>? TransportMessageReceived;

    private void WirePeerEvents(WebRtcPeer peer)
    {
        peer.MessageReceived += OnPeerMessageReceived;
        peer.Opened += (_, _) =>
        {
            if (!ReferenceEquals(_peer, peer)) return;
            var role = _isInitiator ? "offerer" : "answerer";
            _logger.LogInformation("Data channel opened ({Role})", role);
            LogMessage?.Invoke(this, "Data channel connected");
            Connected?.Invoke(this, EventArgs.Empty);
        };
        peer.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_peer, peer)) return;
            var role = _isInitiator ? "offerer" : "answerer";
            _logger.LogInformation("Data channel closed ({Role})", role);
            Disconnected?.Invoke(this, EventArgs.Empty);
        };
        peer.Error += (_, ex) => Error?.Invoke(this, ex);
    }

    private async void OnLocalIceCandidate(object? sender, RTCIceCandidate candidate)
    {
        if (_peer is null) return;

        try
        {
            await _signalingClient.SendIceCandidateAsync(
                _sessionId,
                _peer.PeerId,
                candidate.candidate,
                candidate.sdpMid,
                (int)candidate.sdpMLineIndex);

            _logger.LogDebug("Sent ICE candidate to server: {Candidate}", candidate.candidate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error sending ICE candidate");
        }
    }

    private async void OnOfferReceived(object? sender, OfferReceivedEventArgs e)
    {
        if (_isInitiator) return;

        await _connectionLock.WaitAsync();
        try
        {
            _sessionId = e.SessionId;
            _logger.LogInformation("Received offer from peer {PeerId} for session {SessionId}", e.PeerId, e.SessionId);

            var previous = _peer;
            _peer = null;
            if (previous is not null)
            {
                await previous.DisposeAsync();
            }

            _peer = new WebRtcPeer(
                e.PeerId,
                _config,
                _loggerFactory.CreateLogger<WebRtcPeer>());

            WirePeerEvents(_peer);
            _peer.OnLocalIceCandidate += OnLocalIceCandidate;

            var sdpAnswer = await _peer.CreateAnswerAsync(
                e.SessionId.ToString(),
                e.SdpOffer);

            if (!string.IsNullOrEmpty(sdpAnswer))
            {
                await _signalingClient.SendAnswerAsync(e.SessionId, e.PeerId, sdpAnswer);
                LogMessage?.Invoke(this, "SDP answer created and sent");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling offer");
            Error?.Invoke(this, ex);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async void OnAnswerReceived(object? sender, AnswerReceivedEventArgs e)
    {
        if (!_isInitiator || _peer is null) return;

        await _connectionLock.WaitAsync();
        try
        {
            _logger.LogInformation("Received answer for peer {PeerId}", e.PeerId);
            await _peer.SetRemoteAnswerAsync(e.SdpAnswer);
            LogMessage?.Invoke(this, "SDP answer applied, connecting...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling answer");
            Error?.Invoke(this, ex);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async void OnIceCandidateReceived(object? sender, IceCandidateReceivedEventArgs e)
    {
        if (_peer is null) return;

        try
        {
            await _peer.AddIceCandidateAsync(e.Candidate, e.SdpMid, e.SdpMLineIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error adding ICE candidate from peer {PeerId}", e.PeerId);
        }
    }

    private void OnPeerClosed(object? sender, PeerClosedEventArgs e)
    {
        if (_peer is null || !string.Equals(_peer.PeerId, e.PeerId, StringComparison.Ordinal))
            return;

        _logger.LogInformation("Remote peer {PeerId} closed: {Reason}", e.PeerId, e.Reason);
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void OnSignalingError(object? sender, string error)
    {
        Error?.Invoke(this, new Exception(error));
    }

    private void OnPeerMessageReceived(object? sender, ReadOnlyMemory<byte> data)
    {
        try
        {
            var envelope = TransportEnvelope.Deserialize(data);
            if (envelope is null)
            {
                _logger.LogWarning("Failed to deserialize transport envelope ({Bytes} bytes)", data.Length);
                return;
            }

            if (envelope.MessageType == TransportMessageType.ScreenFrame)
                _logger.LogDebug("ScreenFrame envelope payload {Bytes} bytes", envelope.Payload.Length);

            if (envelope.MessageType == TransportMessageType.Pong
                && envelope.Payload.Length > 0)
            {
                var pong = JsonSerializer.Deserialize<PongMessage>(envelope.Payload.Span);
                if (pong is not null)
                {
                    var rtt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pong.TimestampMs;
                    _logger.LogInformation("Pong received: PingId={PingId}, RTT={Rtt}ms", pong.PingId, rtt);
                    LogMessage?.Invoke(this, $"Pong: RTT={rtt}ms");
                }
            }
            else if (envelope.MessageType == TransportMessageType.Ping
                && envelope.Payload.Length > 0)
            {
                var ping = JsonSerializer.Deserialize<PingMessage>(envelope.Payload.Span);
                if (ping is not null)
                {
                    var pong = new PongMessage
                    {
                        PingId = ping.PingId,
                        TimestampMs = ping.TimestampMs
                    };
                    var pongPayload = JsonSerializer.SerializeToUtf8Bytes(pong);
                    _ = SendAsync(TransportMessageType.Pong, pongPayload);
                }
            }

            TransportMessageReceived?.Invoke(this, envelope);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing received message");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_peer is not null && _peer.PeerId is not null)
            {
                await _signalingClient.ClosePeerConnectionAsync(_sessionId, _peer.PeerId);
                await _peer.DisposeAsync();
            }

            _signalingClient.OfferReceived -= OnOfferReceived;
            _signalingClient.AnswerReceived -= OnAnswerReceived;
            _signalingClient.IceCandidateReceived -= OnIceCandidateReceived;
            _signalingClient.PeerClosed -= OnPeerClosed;
            _signalingClient.Error -= OnSignalingError;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during disposal");
        }
        finally
        {
            _connectionLock.Dispose();
        }
    }
}
