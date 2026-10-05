using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteSupport.Server.Api.Transport.WebRtc;
using SIPSorcery.Net;

namespace RemoteSupport.Server.Tests.Api.Transport;

public class WebRtcTransportTests : IDisposable
{
    private readonly WebRtcPeerManager _peerManager;
    private readonly WebRtcTransportService _service;

    public WebRtcTransportTests()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        _peerManager = new WebRtcPeerManager(loggerFactory.CreateLogger<WebRtcPeerManager>());
        _service = new WebRtcTransportService(_peerManager, loggerFactory.CreateLogger<WebRtcTransportService>());
    }

    public void Dispose() { }

    /// <summary>
    /// Creates two WebRTC peer connections in the same process and connects them via SDP exchange.
    /// Returns both peer connections and diagnostics.
    /// </summary>
    private async Task<(RTCPeerConnection Peer1, RTCPeerConnection Peer2, WebRtcDiagnostics Diag1, WebRtcDiagnostics Diag2)> ConnectPeersAsync(
        RTCConfiguration? config1 = null, RTCConfiguration? config2 = null)
    {
        config1 ??= new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
            }
        };

        config2 ??= config1;

        var pc1 = new RTCPeerConnection(config1);
        var dc1 = await pc1.createDataChannel("test", null);

        var pc2DataChannel = new TaskCompletionSource<RTCDataChannel>();
        var pc2 = new RTCPeerConnection(config2);
        pc2.ondatachannel += (channel) =>
        {
            pc2DataChannel.TrySetResult(channel);
        };

        var offer = pc1.createOffer(new RTCOfferOptions
        {
            X_WaitForIceGatheringToComplete = false
        });
        pc1.setLocalDescription(offer);

        // PC2 receives the offer and sets it as remote description
        var remoteOfferInit = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = offer.sdp
        };
        pc2.setRemoteDescription(remoteOfferInit);

        // PC2 creates an answer
        var answer = pc2.createAnswer(null);
        pc2.setLocalDescription(answer);

        // PC1 receives the answer
        var remoteAnswerInit = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = answer.sdp
        };
        pc1.setRemoteDescription(remoteAnswerInit);

        var connected = new TaskCompletionSource<bool>();
        pc1.onconnectionstatechange += (state) =>
        {
            if (state == RTCPeerConnectionState.connected)
                connected.TrySetResult(true);
            else if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed)
                connected.TrySetResult(false);
        };

        var timeout = Task.Delay(TimeSpan.FromSeconds(15));
        var completed = await Task.WhenAny(connected.Task, timeout);

        var isConnected = completed == connected.Task && connected.Task.Result;
        Assert.True(isConnected, "Peers did not connect within timeout");

        // Wait for connection state to fully propagate
        await Task.Delay(500);

        var diag1 = new WebRtcDiagnostics
        {
            ConnectionState = pc1.connectionState.ToString(),
            IceConnectionState = pc1.iceConnectionState.ToString(),
            ConnectionType = DetermineConnectionType(pc1)
        };
        var diag2 = new WebRtcDiagnostics
        {
            ConnectionState = pc2.connectionState.ToString(),
            IceConnectionState = pc2.iceConnectionState.ToString(),
            ConnectionType = DetermineConnectionType(pc2)
        };

        return (pc1, pc2, diag1, diag2);
    }

    private static string DetermineConnectionType(RTCPeerConnection pc)
    {
        if (pc.iceConnectionState == RTCIceConnectionState.connected)
        {
            return "Connected";
        }
        return pc.iceConnectionState.ToString();
    }

    private static async Task RelayMessagesAsync(RTCDataChannel dc1, RTCDataChannel dc2)
    {
        dc1.onmessage += (chan, type, data) =>
        {
            if (type == DataChannelPayloadProtocols.WebRTC_Binary)
                dc2.send(data);
            else if (type == DataChannelPayloadProtocols.WebRTC_String)
                dc2.send(System.Text.Encoding.UTF8.GetString(data));
        };
        dc2.onmessage += (chan, type, data) =>
        {
            if (type == DataChannelPayloadProtocols.WebRTC_Binary)
                dc1.send(data);
            else if (type == DataChannelPayloadProtocols.WebRTC_String)
                dc1.send(System.Text.Encoding.UTF8.GetString(data));
        };
    }

    // --- Test 1: Local P2P Connection ---

    [Fact]
    public async Task LocalP2P_ConnectionEstablished()
    {
        var (pc1, pc2, diag1, diag2) = await ConnectPeersAsync();

        Assert.Equal("connected", diag1.ConnectionState);
        Assert.Equal("connected", diag2.ConnectionState);
        Assert.Contains("Connected", diag1.ConnectionType);

        pc1.Close("test");
        pc2.Close("test");
    }

    // --- Test 2: Data Channel Send/Receive ---

    [Fact]
    public async Task DataChannel_SendReceive_Works()
    {
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
            }
        };

        var pc1 = new RTCPeerConnection(config);
        var dc1 = await pc1.createDataChannel("test", null);

        var receivedMessages = new ConcurrentBag<string>();
        var received = new TaskCompletionSource<int>();
        int expectedCount = 3;

        var pc2 = new RTCPeerConnection(config);
        pc2.ondatachannel += async (channel) =>
        {
            channel.onmessage += (chan, type, data) =>
            {
                if (type == DataChannelPayloadProtocols.WebRTC_String)
                {
                    var msg = System.Text.Encoding.UTF8.GetString(data);
                    receivedMessages.Add(msg);
                    if (receivedMessages.Count >= expectedCount)
                        received.TrySetResult(receivedMessages.Count);
                }
            };
        };

        var offer = pc1.createOffer(new RTCOfferOptions { X_WaitForIceGatheringToComplete = false });
        pc1.setLocalDescription(offer);
        pc2.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offer.sdp });
        var answer = pc2.createAnswer(null);
        pc2.setLocalDescription(answer);
        pc1.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer.sdp });

        var connected = new TaskCompletionSource<bool>();
        pc1.onconnectionstatechange += (state) =>
        {
            if (state == RTCPeerConnectionState.connected) connected.TrySetResult(true);
            else if (state == RTCPeerConnectionState.failed) connected.TrySetResult(false);
        };

        var timeout = Task.Delay(TimeSpan.FromSeconds(15));
        await Task.WhenAny(connected.Task, timeout);
        Assert.True(connected.Task.Result, "Connection failed");

        // Wait for data channel to be ready
        var dc2Ready = new TaskCompletionSource<bool>();
        pc2.ondatachannel += async (channel) =>
        {
            channel.onopen += () => dc2Ready.TrySetResult(true);
            if (channel.readyState == RTCDataChannelState.open)
                dc2Ready.TrySetResult(true);
        };

        await Task.WhenAny(dc2Ready.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await Task.Delay(200);

        dc1.send("message1");
        dc1.send("message2");
        dc1.send("message3");

        var recvTimeout = Task.Delay(TimeSpan.FromSeconds(5));
        var recvResult = await Task.WhenAny(received.Task, recvTimeout);
        Assert.Equal(received.Task, recvResult);
        Assert.Equal(3, received.Task.Result);
        Assert.Contains("message1", receivedMessages);
        Assert.Contains("message2", receivedMessages);
        Assert.Contains("message3", receivedMessages);

        pc1.Close("test");
        pc2.Close("test");
    }

    // --- Test 3: Binary Data Channel ---

    [Fact]
    public async Task DataChannel_BinarySendReceive_Works()
    {
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
            }
        };

        var pc1 = new RTCPeerConnection(config);
        var dc1 = await pc1.createDataChannel("binary-test", null);

        var receivedData = new TaskCompletionSource<byte[]>();

        var pc2 = new RTCPeerConnection(config);
        pc2.ondatachannel += async (channel) =>
        {
            channel.onmessage += (chan, type, data) =>
            {
                if (type == DataChannelPayloadProtocols.WebRTC_Binary)
                    receivedData.TrySetResult(data);
            };
        };

        var offer = pc1.createOffer(new RTCOfferOptions { X_WaitForIceGatheringToComplete = false });
        pc1.setLocalDescription(offer);
        pc2.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offer.sdp });
        var answer = pc2.createAnswer(null);
        pc2.setLocalDescription(answer);
        pc1.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer.sdp });

        var connected = new TaskCompletionSource<bool>();
        pc1.onconnectionstatechange += (state) =>
        {
            if (state == RTCPeerConnectionState.connected) connected.TrySetResult(true);
            else if (state == RTCPeerConnectionState.failed) connected.TrySetResult(false);
        };

        var timeout = Task.Delay(TimeSpan.FromSeconds(15));
        await Task.WhenAny(connected.Task, timeout);
        Assert.True(connected.Task.Result);

        // Wait for data channel to be ready
        var dc2Ready = new TaskCompletionSource<bool>();
        pc2.ondatachannel += async (channel) =>
        {
            channel.onopen += () => dc2Ready.TrySetResult(true);
            if (channel.readyState == RTCDataChannelState.open)
                dc2Ready.TrySetResult(true);
        };

        await Task.WhenAny(dc2Ready.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await Task.Delay(200);

        var testData = new byte[] { 0x01, 0x02, 0x03, 0xFF, 0xFE, 0xFD, 0x00, 0xAB, 0xCD, 0xEF };
        dc1.send(testData);

        var recvTimeout = Task.Delay(TimeSpan.FromSeconds(5));
        var recvResult = await Task.WhenAny(receivedData.Task, recvTimeout);
        Assert.Equal(receivedData.Task, recvResult);
        Assert.Equal(testData, receivedData.Task.Result);

        pc1.Close("test");
        pc2.Close("test");
    }

    // --- Test 4: P2P Connection Using STUN ---

    [Fact]
    public async Task STUN_P2P_ConnectionEstablished()
    {
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun1.l.google.com:19302" }
            }
        };

        var (pc1, pc2, diag1, diag2) = await ConnectPeersAsync(config, config);

        Assert.Equal("connected", diag1.ConnectionState);
        Assert.Contains("Connected", diag1.ConnectionType);

        pc1.Close("test");
        pc2.Close("test");
    }

    // --- Test 5: WebRTC Service Create/Set Operations ---

    [Fact]
    public async Task WebRtcService_CreateOffer_ReturnsValidOffer()
    {
        var result = await _service.CreateOfferAsync("test-session-1", "agent");

        Assert.False(string.IsNullOrEmpty(result.PeerId));
        Assert.False(string.IsNullOrEmpty(result.SdpOffer));
        Assert.Contains("v=0", result.SdpOffer);
        Assert.Contains("a=group:BUNDLE", result.SdpOffer);

        await _service.ClosePeerAsync(result.PeerId, "test");
    }

    [Fact]
    public async Task WebRtcService_SetAnswer_WithValidPeer_Succeeds()
    {
        var offerResult = await _service.CreateOfferAsync("test-session-2", "agent");

        var pc2 = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
            }
        });

        // PC2 must receive the offer first, then create answer
        pc2.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerResult.SdpOffer });
        var answer = pc2.createAnswer(null);
        pc2.setLocalDescription(answer);

        var setResult = await _service.SetAnswerAsync(offerResult.PeerId, answer.sdp);

        Assert.True(setResult.IsSuccess);

        await _service.ClosePeerAsync(offerResult.PeerId, "test");
        pc2.Close("test");
    }

    [Fact]
    public async Task WebRtcService_SetAnswer_WithInvalidPeer_Fails()
    {
        var result = await _service.SetAnswerAsync("nonexistent-peer", "v=0...");

        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.ErrorMessage!);
    }

    // --- Test 6: Session Isolation ---

    [Fact]
    public async Task SessionIsolation_DifferentSessions_AreIsolated()
    {
        var offer1 = await _service.CreateOfferAsync("session-A", "agent");
        var offer2 = await _service.CreateOfferAsync("session-B", "agent");

        var diag1 = _service.GetDiagnostics(offer1.PeerId);
        var diag2 = _service.GetDiagnostics(offer2.PeerId);

        Assert.Equal("session-A", diag1.SessionId);
        Assert.Equal("session-B", diag2.SessionId);
        Assert.NotEqual(diag1.PeerId, diag2.PeerId);

        await _service.ClosePeerAsync(offer1.PeerId, "test");
        await _service.ClosePeerAsync(offer2.PeerId, "test");
    }

    // --- Test 7: Connection Closure ---

    [Fact]
    public async Task ConnectionClosure_CleansUpProperly()
    {
        var offer = await _service.CreateOfferAsync("test-session-close", "agent");

        var diagBefore = _service.GetDiagnostics(offer.PeerId);
        Assert.Equal("new", diagBefore.IceConnectionState);

        await _service.ClosePeerAsync(offer.PeerId, "test_close");

        var diagAfter = _service.GetDiagnostics(offer.PeerId);
        Assert.Equal("NotFound", diagAfter.ConnectionState);
    }

    // --- Test 8: Diagnostics ---

    [Fact]
    public async Task Diagnostics_ReturnCorrectValues()
    {
        var offer = await _service.CreateOfferAsync("test-session-diag", "agent");

        var diag = _service.GetDiagnostics(offer.PeerId);

        Assert.Equal(offer.PeerId, diag.PeerId);
        Assert.Equal("test-session-diag", diag.SessionId);
        Assert.Equal("agent", diag.Role);
        Assert.NotNull(diag.ConnectionState);

        await _service.ClosePeerAsync(offer.PeerId, "test");
    }

    // --- Test 9: Multiple Peers Per Session ---

    [Fact]
    public async Task MultiplePeers_CanExistPerSession()
    {
        var offer1 = await _service.CreateOfferAsync("multi-peer-session", "agent");
        var offer2 = await _service.CreateOfferAsync("multi-peer-session", "customer");

        Assert.NotEqual(offer1.PeerId, offer2.PeerId);

        var diag1 = _service.GetDiagnostics(offer1.PeerId);
        var diag2 = _service.GetDiagnostics(offer2.PeerId);

        Assert.Equal("agent", diag1.Role);
        Assert.Equal("customer", diag2.Role);

        await _service.ClosePeerAsync(offer1.PeerId, "test");
        await _service.ClosePeerAsync(offer2.PeerId, "test");
    }

    // --- Test 10: WebRTC Offer Contains Expected SDP Fields ---

    [Fact]
    public async Task Offer_ContainsRequiredSdpFields()
    {
        var offer = await _service.CreateOfferAsync("test-sdp-fields", "agent");

        Assert.Contains("v=0", offer.SdpOffer);
        Assert.Contains("a=group:BUNDLE", offer.SdpOffer);
        Assert.Contains("a=fingerprint:", offer.SdpOffer);
        Assert.Contains("a=ice-ufrag:", offer.SdpOffer);
        Assert.Contains("a=ice-pwd:", offer.SdpOffer);
        Assert.Contains("a=sctp-port:", offer.SdpOffer);
        Assert.Contains("a=max-message-size:", offer.SdpOffer);

        await _service.ClosePeerAsync(offer.PeerId, "test");
    }

    // --- Test 11: Graceful Shutdown of Multiple Peers ---

    [Fact]
    public async Task GracefulShutdown_AllPeersClosedCleanly()
    {
        var peers = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var offer = await _service.CreateOfferAsync($"shutdown-session-{i}", "agent");
            peers.Add(offer.PeerId);
        }

        foreach (var peerId in peers)
        {
            await _service.ClosePeerAsync(peerId, "graceful_shutdown");
        }

        foreach (var peerId in peers)
        {
            var diag = _service.GetDiagnostics(peerId);
            Assert.Equal("NotFound", diag.ConnectionState);
        }
    }

    // --- Test 12: Connection Type Detection ---

    [Fact]
    public async Task ConnectionType_LocalP2P_DetectedCorrectly()
    {
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
            }
        };

        var (pc1, pc2, diag1, diag2) = await ConnectPeersAsync(config, config);

        Assert.Equal("connected", diag1.ConnectionState);
        Assert.Contains("Connected", diag1.ConnectionType);

        pc1.Close("test");
        pc2.Close("test");
    }
}
