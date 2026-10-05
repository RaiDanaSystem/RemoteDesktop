using RemoteSupport.Shared.Transport;

namespace RemoteSupport.Server.Tests.Transport;

public class WebRtcConfigurationWithTurnTests
{
    [Fact]
    public void WithTurn_IncludesTurnServer()
    {
        var config = WebRtcConfiguration.WithTurn(
            "turn:turn.raidana.local:3478",
            "user",
            "pass");

        Assert.Contains(config.IceServers, s => s.Urls.Contains("turn:turn.raidana.local:3478"));
        var turnServer = config.IceServers.First(s => s.Urls.Contains("turn:turn.raidana.local:3478"));
        Assert.Equal("user", turnServer.Username);
        Assert.Equal("pass", turnServer.Credential);
    }

    [Fact]
    public void WithTurn_IncludesStunServer()
    {
        var config = WebRtcConfiguration.WithTurn(
            "turn:turn.raidana.local:3478",
            "user",
            "pass",
            stunUrl: "stun:stun.l.google.com:19302");

        Assert.Contains(config.IceServers, s => s.Urls.Contains("stun:stun.l.google.com:19302"));
    }

    [Fact]
    public void WithTurn_NoStunProvided_AddsDefaultStun()
    {
        var config = WebRtcConfiguration.WithTurn(
            "turn:turn.raidana.local:3478",
            "user",
            "pass");

        Assert.Contains(config.IceServers, s => s.Urls.Contains("stun:stun.l.google.com:19302"));
    }

    [Fact]
    public void RelayOnly_ContainsOnlyTurn()
    {
        var config = WebRtcConfiguration.RelayOnly(
            "turn:turn.raidana.local:3478",
            "user",
            "pass");

        Assert.Single(config.IceServers);
        Assert.Equal("turn:turn.raidana.local:3478", config.IceServers[0].Urls[0]);
    }

    [Fact]
    public void Default_ContainsOnlyGoogleStun()
    {
        var config = WebRtcConfiguration.Default;
        Assert.Single(config.IceServers);
        Assert.Equal("stun:stun.l.google.com:19302", config.IceServers[0].Urls[0]);
    }
}
