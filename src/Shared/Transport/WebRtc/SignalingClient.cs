using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace RemoteSupport.Shared.Transport.WebRtc;

/// <summary>
/// SignalR-based WebRTC signaling client. Connects to the server's WebRtcSignalingHub
/// and relays SDP/ICE messages between peers.
/// Works for both CustomerAgent (answering) and SupportAgent (initiating).
/// </summary>
public sealed class SignalingClient : IWebRtcSignalingClient
{
    private HubConnection? _connection;
    private readonly ILogger<SignalingClient> _logger;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public event EventHandler<OfferReceivedEventArgs>? OfferReceived;
    public event EventHandler<AnswerReceivedEventArgs>? AnswerReceived;
    public event EventHandler<IceCandidateReceivedEventArgs>? IceCandidateReceived;
    public event EventHandler<PeerClosedEventArgs>? PeerClosed;
    public event EventHandler<string>? Error;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;

    public SignalingClient(ILogger<SignalingClient> logger)
    {
        _logger = logger;
    }

    public async Task ConnectAsync(string serverUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        var hubUrl = $"{serverUrl.TrimEnd('/')}/hubs/webrtc";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(accessToken)!;
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero, TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)
            })
            .Build();

        RegisterHandlers(_connection);

        _connection.Closed += async error =>
        {
            _logger.LogWarning("Signaling connection closed: {Error}", error?.Message);
            Disconnected?.Invoke(this, EventArgs.Empty);
        };

        _connection.Reconnected += connectionId =>
        {
            _logger.LogInformation("Signaling reconnected: {ConnectionId}", connectionId);
            Connected?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        };

        try
        {
            await _connection.StartAsync(cancellationToken);
            Connected?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation("Signaling client connected to {Url}", hubUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect signaling to {Url}", hubUrl);
            Error?.Invoke(this, $"Connection failed: {ex.Message}");
        }
    }

    public async Task RequestOfferAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            _logger.LogWarning("Cannot request offer: not connected");
            return;
        }

        await _connection.InvokeCoreAsync("RequestOffer", typeof(object), new object[] { sessionId }, cancellationToken);
    }

    public async Task SubmitOfferAsync(Guid sessionId, string peerId, string sdpOffer, CancellationToken cancellationToken = default)
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            _logger.LogWarning("Cannot submit offer: not connected");
            return;
        }

        await _connection.InvokeCoreAsync("SubmitOffer", typeof(object), new object[] { sessionId, peerId, sdpOffer }, cancellationToken);
    }

    public async Task SendAnswerAsync(Guid sessionId, string peerId, string sdpAnswer, CancellationToken cancellationToken = default)
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            _logger.LogWarning("Cannot send answer: not connected");
            return;
        }

        await _connection.InvokeCoreAsync("SubmitAnswer", typeof(object), new object[] { sessionId, peerId, sdpAnswer }, cancellationToken);
    }

    public async Task SendIceCandidateAsync(Guid sessionId, string peerId, string candidate, string sdpMid, int sdpMLineIndex, CancellationToken cancellationToken = default)
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            _logger.LogWarning("Cannot send ICE candidate: not connected");
            return;
        }

        await _connection.InvokeCoreAsync("SubmitIceCandidate", typeof(object), new object[] { sessionId, peerId, candidate, sdpMid, sdpMLineIndex }, cancellationToken);
    }

    public async Task ClosePeerConnectionAsync(Guid sessionId, string peerId, CancellationToken cancellationToken = default)
    {
        if (_connection?.State != HubConnectionState.Connected) return;

        await _connection.InvokeCoreAsync("CloseConnection", typeof(object), new object[] { sessionId, peerId }, cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            await _connection.StopAsync(cancellationToken);
        }
    }

    private void RegisterHandlers(HubConnection connection)
    {
        connection.On("OfferReceived", (JsonElement data) =>
        {
            try
            {
                var peerId = data.GetProperty("peerId").GetString() ?? "";
                var sdpOffer = data.GetProperty("sdpOffer").GetString() ?? "";
                var sessionId = ReadGuid(data, "sessionId");
                IceServerConfig[]? iceServers = null;
                try
                {
                    if (data.TryGetProperty("iceServers", out var iceServersElement)
                        && iceServersElement.ValueKind == JsonValueKind.Array)
                    {
                        iceServers = JsonSerializer.Deserialize<IceServerConfig[]>(iceServersElement.GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                }
                catch (Exception iceEx)
                {
                    _logger.LogWarning(iceEx, "Ignoring invalid iceServers in OfferReceived");
                }

                _logger.LogInformation("Offer received from server, peer {PeerId}, session {SessionId}", peerId, sessionId);

                OfferReceived?.Invoke(this, new OfferReceivedEventArgs
                {
                    PeerId = peerId,
                    SessionId = sessionId,
                    SdpOffer = sdpOffer,
                    IceServers = iceServers
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing OfferReceived");
                Error?.Invoke(this, $"Error parsing offer: {ex.Message}");
            }
        });

        connection.On("AnswerReceived", (JsonElement data) =>
        {
            try
            {
                var peerId = data.GetProperty("peerId").GetString() ?? "";
                var sdpAnswer = data.GetProperty("sdpAnswer").GetString() ?? "";
                var sessionId = ReadGuid(data, "sessionId");

                _logger.LogInformation("Answer received for peer {PeerId}, session {SessionId}", peerId, sessionId);

                AnswerReceived?.Invoke(this, new AnswerReceivedEventArgs
                {
                    PeerId = peerId,
                    SessionId = sessionId,
                    SdpAnswer = sdpAnswer
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing AnswerReceived");
                Error?.Invoke(this, $"Error parsing answer: {ex.Message}");
            }
        });

        connection.On("IceCandidateReceived", (JsonElement data) =>
        {
            try
            {
                var peerId = data.GetProperty("peerId").GetString() ?? "";
                var candidate = data.GetProperty("candidate").GetString() ?? "";
                var sdpMid = data.GetProperty("sdpMid").GetString() ?? "";
                var sdpMLineIndex = data.GetProperty("sdpMLineIndex").GetInt32();

                IceCandidateReceived?.Invoke(this, new IceCandidateReceivedEventArgs
                {
                    PeerId = peerId,
                    Candidate = candidate,
                    SdpMid = sdpMid,
                    SdpMLineIndex = sdpMLineIndex
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing IceCandidateReceived");
            }
        });

        connection.On("PeerClosed", (JsonElement data) =>
        {
            try
            {
                var peerId = data.GetProperty("peerId").GetString() ?? "";
                var reason = data.TryGetProperty("reason", out var r) ? r.GetString() : null;

                _logger.LogInformation("Peer closed: {PeerId}, reason: {Reason}", peerId, reason);

                PeerClosed?.Invoke(this, new PeerClosedEventArgs
                {
                    PeerId = peerId,
                    Reason = reason
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing PeerClosed");
            }
        });

        connection.On("Error", (JsonElement data) =>
        {
            try
            {
                var msg = data.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                _logger.LogWarning("Server error: {Message}", msg);
                Error?.Invoke(this, msg ?? "Unknown error");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing Error message");
            }
        });

        connection.On("OfferRequested", (JsonElement data) =>
        {
            _logger.LogInformation("Offer request acknowledged by server");
        });
    }

    private static Guid ReadGuid(JsonElement data, string propertyName)
    {
        if (!data.TryGetProperty(propertyName, out var element))
            return Guid.Empty;

        if (element.ValueKind == JsonValueKind.String)
            return Guid.TryParse(element.GetString(), out var parsed) ? parsed : Guid.Empty;

        return element.TryGetGuid(out var guid) ? guid : Guid.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
