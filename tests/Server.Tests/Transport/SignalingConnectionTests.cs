using Microsoft.Extensions.Logging;
using RemoteSupport.Shared.Transport.WebRtc;
using Moq;

namespace RemoteSupport.Server.Tests.Transport;

public class SignalingConnectionTests
{
    [Fact]
    public void WebRtcSessionManager_IsSignalingConnected_ReflectsClientState()
    {
        var mockClientFactory = new Mock<ILoggerFactory>();
        mockClientFactory.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(Mock.Of<ILogger>());

        var mockSignalingLogger = Mock.Of<ILogger<SignalingClient>>();
        var mockPeerLogger = Mock.Of<ILogger<WebRtcPeer>>();

        var signalingClient = new SignalingClient(mockSignalingLogger);
        var config = RemoteSupport.Shared.Transport.WebRtcConfiguration.Default;

        var manager = new WebRtcSessionManager(signalingClient, config, mockClientFactory.Object);

        Assert.False(manager.IsSignalingConnected);
    }

    [Fact]
    public async Task WebRtcSessionManager_ConnectSignalingAsync_Connected_ReturnsEarly()
    {
        var mockClientFactory = new Mock<ILoggerFactory>();
        mockClientFactory.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(Mock.Of<ILogger>());

        var mockSignalingLogger = Mock.Of<ILogger<SignalingClient>>();
        var signalingClient = new SignalingClient(mockSignalingLogger);
        var config = RemoteSupport.Shared.Transport.WebRtcConfiguration.Default;

        var manager = new WebRtcSessionManager(signalingClient, config, mockClientFactory.Object);

        await manager.ConnectSignalingAsync("http://localhost:5096", "fake-token");
        Assert.False(manager.IsSignalingConnected);
    }
}
