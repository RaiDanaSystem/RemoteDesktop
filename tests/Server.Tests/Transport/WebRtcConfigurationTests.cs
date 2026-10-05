using RemoteSupport.Shared.Transport;

namespace RemoteSupport.Server.Tests.Transport;

public class WebRtcConfigurationTests
{
    [Fact]
    public void Default_HasGoogleStun()
    {
        var config = WebRtcConfiguration.Default;
        Assert.Single(config.IceServers);
        Assert.Contains("stun.l.google.com", config.IceServers[0].Urls[0]);
    }

    [Fact]
    public void IceServerConfig_CanStoreTurnCredentials()
    {
        var config = new WebRtcConfiguration
        {
            IceServers = new List<IceServerConfig>
            {
                new() { Urls = new[] { "stun:stun.l.google.com:19302" } },
                new()
                {
                    Urls = new[] { "turn:turn.example.com:3478" },
                    Username = "testuser",
                    Credential = "testpass"
                }
            }
        };

        Assert.Equal(2, config.IceServers.Count);
        Assert.Null(config.IceServers[0].Username);
        Assert.Equal("testuser", config.IceServers[1].Username);
        Assert.Equal("testpass", config.IceServers[1].Credential);
    }

    [Fact]
    public void IDataChannel_States_AreDefined()
    {
        var states = Enum.GetValues<DataChannelState>();
        Assert.Equal(6, states.Length);
        Assert.Contains(DataChannelState.New, states);
        Assert.Contains(DataChannelState.Connecting, states);
        Assert.Contains(DataChannelState.Connected, states);
        Assert.Contains(DataChannelState.Closing, states);
        Assert.Contains(DataChannelState.Closed, states);
        Assert.Contains(DataChannelState.Failed, states);
    }
}
