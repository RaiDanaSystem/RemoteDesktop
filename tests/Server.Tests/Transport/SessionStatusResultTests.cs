using RemoteSupport.Server.Application.Interfaces;

namespace RemoteSupport.Server.Tests.Transport;

public class SessionStatusResultTests
{
    [Fact]
    public void SessionStatusResult_HasAgentUserId()
    {
        var result = new SessionStatusResult
        {
            SessionExists = true,
            SessionId = Guid.NewGuid(),
            AgentUserId = Guid.NewGuid(),
            AgentName = "admin",
            AgentDisplayName = "System Administrator",
            CustomerDeviceName = "customer-pc",
            CustomerDeviceIdentifier = "customer-pc",
            Status = "Active"
        };

        Assert.True(result.AgentUserId.HasValue);
        Assert.NotEqual(Guid.Empty, result.AgentUserId.Value);
        Assert.Equal("admin", result.AgentName);
        Assert.Equal("customer-pc", result.CustomerDeviceName);
    }

    [Fact]
    public void ConnectionRequestPollResult_HasAgentUserId()
    {
        var result = new ConnectionRequestPollResult
        {
            HasPendingRequest = true,
            SessionId = Guid.NewGuid(),
            AgentUserId = Guid.NewGuid(),
            AgentName = "admin",
            AgentRole = "Admin"
        };

        Assert.True(result.AgentUserId.HasValue);
        Assert.NotEqual(Guid.Empty, result.AgentUserId.Value);
    }
}
