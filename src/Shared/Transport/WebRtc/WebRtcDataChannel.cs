using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace RemoteSupport.Shared.Transport.WebRtc;

/// <summary>
/// Client-side WebRTC implementation that wraps SIPSorcery RTCPeerConnection.
/// Creates a peer connection, handles SDP/ICE exchange via signaling client,
/// and exposes a clean IDataChannel for feature managers.
/// </summary>
public sealed class WebRtcPeer : IDataChannel
{
    private readonly ILogger<WebRtcPeer> _logger;
    private readonly WebRtcConfiguration _config;
    private RTCPeerConnection? _peerConnection;
    private RTCDataChannel? _dataChannel;
    private readonly string _peerId;
    private string? _sessionId;
    private DataChannelState _state = DataChannelState.New;
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;
    private bool _remoteDescriptionSet;
    private readonly ConcurrentQueue<(string Candidate, string SdpMid, int SdpMLineIndex)> _pendingIceCandidates = new();

    public string PeerId => _peerId;
    public string? SessionId => _sessionId;

    public DataChannelState State
    {
        get => _state;
        private set
        {
            lock (_stateLock)
            {
                if (_state == value) return;
                _state = value;
                _logger.LogDebug("Peer {PeerId} state changed to {State}", _peerId, value);
            }
        }
    }

    public event EventHandler<ReadOnlyMemory<byte>>? MessageReceived;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<Exception>? Error;
    public event EventHandler<RTCIceCandidate>? OnLocalIceCandidate;

    public WebRtcPeer(string peerId, WebRtcConfiguration config, ILogger<WebRtcPeer> logger)
    {
        _peerId = peerId;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Creates an SDP offer. Used by the initiating peer (SupportAgent).
    /// </summary>
    public async Task<string> CreateOfferAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessionId = sessionId;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var iceServers = _config.IceServers.Select(s => new RTCIceServer
        {
            urls = s.Urls.FirstOrDefault() ?? "",
            username = s.Username,
            credential = s.Credential
        }).ToList();

        var config = new RTCConfiguration { iceServers = iceServers };
        _peerConnection = new RTCPeerConnection(config);

        SetupPeerConnectionHandlers();

        _dataChannel = await _peerConnection.createDataChannel("data", null);
        SetupDataChannelHandlers(_dataChannel);

        State = DataChannelState.Connecting;

        var offer = _peerConnection.createOffer(new RTCOfferOptions
        {
            X_WaitForIceGatheringToComplete = false
        });

        await _peerConnection.setLocalDescription(offer);

        _logger.LogInformation("Created SDP offer for session {SessionId}, peer {PeerId}", sessionId, _peerId);

        return offer.sdp;
    }

    /// <summary>
    /// Sets a remote SDP offer and creates an answer. Used by the responding peer (CustomerAgent).
    /// </summary>
    public async Task<string> CreateAnswerAsync(string sessionId, string sdpOffer, CancellationToken cancellationToken = default)
    {
        _sessionId = sessionId;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var iceServers = _config.IceServers.Select(s => new RTCIceServer
        {
            urls = s.Urls.FirstOrDefault() ?? "",
            username = s.Username,
            credential = s.Credential
        }).ToList();

        var config = new RTCConfiguration { iceServers = iceServers };
        _peerConnection = new RTCPeerConnection(config);

        SetupPeerConnectionHandlers();

        _peerConnection.ondatachannel += (channel) =>
        {
            _logger.LogInformation("Received data channel: {Label}", channel.label);
            _dataChannel = channel;
            SetupDataChannelHandlers(channel);
            if (channel.readyState == RTCDataChannelState.open)
            {
                _logger.LogInformation("Data channel already open for peer {PeerId}", _peerId);
                State = DataChannelState.Connected;
                Opened?.Invoke(this, EventArgs.Empty);
            }
        };

        State = DataChannelState.Connecting;

        var remoteOfferInit = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = sdpOffer
        };

        var result = _peerConnection.setRemoteDescription(remoteOfferInit);
        if (result != SetDescriptionResultEnum.OK)
        {
            _logger.LogError("Failed to set remote description: {Result}", result);
            State = DataChannelState.Failed;
            return string.Empty;
        }

        _remoteDescriptionSet = true;
        DrainPendingIceCandidates();

        var answer = _peerConnection.createAnswer(null);
        await _peerConnection.setLocalDescription(answer);

        _logger.LogInformation("Created SDP answer for session {SessionId}, peer {PeerId}", sessionId, _peerId);

        return answer.sdp;
    }

    /// <summary>
    /// Sets a remote SDP answer. Used after creating an offer.
    /// </summary>
    public Task SetRemoteAnswerAsync(string sdpAnswer, CancellationToken cancellationToken = default)
    {
        if (_peerConnection is null)
            throw new InvalidOperationException("Peer connection not created.");

        var remoteAnswerInit = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = sdpAnswer
        };

        var result = _peerConnection.setRemoteDescription(remoteAnswerInit);
        if (result != SetDescriptionResultEnum.OK)
        {
            _logger.LogError("Failed to set remote answer: {Result}", result);
            State = DataChannelState.Failed;
        }
        else
        {
            _remoteDescriptionSet = true;
            DrainPendingIceCandidates();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds a remote ICE candidate received from the peer via signaling.
    /// Buffers candidates that arrive before remote description is set.
    /// </summary>
    public Task AddIceCandidateAsync(string candidate, string sdpMid, int sdpMLineIndex, CancellationToken cancellationToken = default)
    {
        if (_peerConnection is null)
        {
            _logger.LogDebug("Buffering ICE candidate (no peer connection yet): {Candidate}", candidate);
            _pendingIceCandidates.Enqueue((candidate, sdpMid, sdpMLineIndex));
            return Task.CompletedTask;
        }

        if (!_remoteDescriptionSet)
        {
            _logger.LogDebug("Buffering ICE candidate (remote description not set): {Candidate}", candidate);
            _pendingIceCandidates.Enqueue((candidate, sdpMid, sdpMLineIndex));
            return Task.CompletedTask;
        }

        ApplyIceCandidate(candidate, sdpMid, sdpMLineIndex);
        return Task.CompletedTask;
    }

    private void ApplyIceCandidate(string candidate, string sdpMid, int sdpMLineIndex)
    {
        try
        {
            var iceCandidate = new RTCIceCandidateInit
            {
                candidate = candidate,
                sdpMid = sdpMid,
                sdpMLineIndex = (ushort)sdpMLineIndex
            };

            _peerConnection!.addIceCandidate(iceCandidate);
            _logger.LogDebug("Applied ICE candidate: {Candidate}", candidate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply ICE candidate: {Candidate}", candidate);
        }
    }

    private void DrainPendingIceCandidates()
    {
        while (_pendingIceCandidates.TryDequeue(out var pending))
        {
            _logger.LogDebug("Draining buffered ICE candidate: {Candidate}", pending.Candidate);
            ApplyIceCandidate(pending.Candidate, pending.SdpMid, pending.SdpMLineIndex);
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_dataChannel is null || _state != DataChannelState.Connected)
        {
            _logger.LogWarning("Cannot send: data channel not ready (state={State})", _state);
            return;
        }

        try
        {
            _dataChannel.send(data.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending data on peer {PeerId}", _peerId);
            Error?.Invoke(this, ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_state == DataChannelState.Closed || _state == DataChannelState.Closing)
            return;

        State = DataChannelState.Closing;

        try
        {
            _cts?.Cancel();

            _dataChannel?.close();
            _dataChannel = null;

            if (_peerConnection is not null)
            {
                _peerConnection.Close("dispose");
                _peerConnection.Dispose();
                _peerConnection = null;
            }

            State = DataChannelState.Closed;
            Closed?.Invoke(this, EventArgs.Empty);

            _logger.LogInformation("Peer {PeerId} disposed", _peerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing peer {PeerId}", _peerId);
            State = DataChannelState.Closed;
        }
        finally
        {
            _cts?.Dispose();
        }
    }

    private void SetupPeerConnectionHandlers()
    {
        _peerConnection!.onconnectionstatechange += (state) =>
        {
            _logger.LogInformation("Peer {PeerId} connection state: {State}", _peerId, state);

            switch (state)
            {
                case RTCPeerConnectionState.connected:
                    State = DataChannelState.Connected;
                    break;
                case RTCPeerConnectionState.disconnected:
                case RTCPeerConnectionState.failed:
                    State = DataChannelState.Failed;
                    Error?.Invoke(this, new Exception($"Connection state: {state}"));
                    break;
                case RTCPeerConnectionState.closed:
                    State = DataChannelState.Closed;
                    break;
            }
        };

        _peerConnection!.oniceconnectionstatechange += (state) =>
        {
            _logger.LogDebug("Peer {PeerId} ICE state: {State}", _peerId, state);
        };

        _peerConnection!.onicecandidate += (candidate) =>
        {
            if (candidate is not null)
            {
                _logger.LogDebug("ICE candidate gathered for peer {PeerId}: {Candidate}", _peerId, candidate.candidate);
                OnLocalIceCandidate?.Invoke(this, candidate);
            }
        };

        _peerConnection!.onicegatheringstatechange += (state) =>
        {
            _logger.LogDebug("Peer {PeerId} ICE gathering: {State}", _peerId, state);
        };
    }

    private void SetupDataChannelHandlers(RTCDataChannel channel)
    {
        channel.onopen += () =>
        {
            _logger.LogInformation("Data channel opened for peer {PeerId}", _peerId);
            State = DataChannelState.Connected;
            Opened?.Invoke(this, EventArgs.Empty);
        };

        channel.onclose += () =>
        {
            _logger.LogInformation("Data channel closed for peer {PeerId}", _peerId);
            State = DataChannelState.Closed;
            Closed?.Invoke(this, EventArgs.Empty);
        };

        channel.onmessage += (chan, type, data) =>
        {
            try
            {
                var bytes = data.ToArray();
                MessageReceived?.Invoke(this, bytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling message from peer {PeerId}", _peerId);
                Error?.Invoke(this, ex);
            }
        };

        channel.onerror += (error) =>
        {
            _logger.LogError("Data channel error for peer {PeerId}: {Error}", _peerId, error);
            Error?.Invoke(this, new Exception($"DataChannel error: {error}"));
        };
    }
}
