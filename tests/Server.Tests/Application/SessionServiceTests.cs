using RemoteSupport.Server.Application.Services;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Tests.Application;

public class SessionServiceTests : IDisposable
{
    private readonly RemoteSupport.Server.Infrastructure.Persistence.ApplicationDbContext _context;
    private readonly SessionService _service;

    public SessionServiceTests()
    {
        _context = TestHelpers.CreateInMemoryContext();
        _service = new SessionService(_context);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task CreateSession_CreatesNewSession()
    {
        var agentUserId = Guid.NewGuid();

        var session = await _service.CreateSessionAsync(
            agentUserId, "device-001", "Customer PC", "Windows 11");

        Assert.NotEqual(Guid.Empty, session.Id);
        Assert.Equal(agentUserId, session.AgentUserId);
        Assert.Equal("device-001", session.CustomerDeviceIdentifier);
        Assert.Equal(SessionStatus.Pending, session.Status);
    }

    [Fact]
    public async Task GetSession_ReturnsNull_WhenNotFound()
    {
        var result = await _service.GetSessionAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task GetSession_ReturnsSession_WhenExists()
    {
        var created = await _service.CreateSessionAsync(
            Guid.NewGuid(), "device-001", null, null);

        var found = await _service.GetSessionAsync(created.Id);

        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Id);
    }

    [Fact]
    public async Task UpdateSessionStatus_ChangesStatus()
    {
        var session = await _service.CreateSessionAsync(
            Guid.NewGuid(), "device-001", null, null);

        var updated = await _service.UpdateSessionStatusAsync(
            session.Id, SessionStatus.Active);

        Assert.NotNull(updated);
        Assert.Equal(SessionStatus.Active, updated!.Status);
        Assert.NotNull(updated.StartedAtUtc);
    }

    [Fact]
    public async Task UpdateSessionStatus_EndsSession()
    {
        var session = await _service.CreateSessionAsync(
            Guid.NewGuid(), "device-001", null, null);
        await _service.UpdateSessionStatusAsync(session.Id, SessionStatus.Active);

        var ended = await _service.UpdateSessionStatusAsync(
            session.Id, SessionStatus.Ended, "Customer disconnected");

        Assert.NotNull(ended);
        Assert.Equal(SessionStatus.Ended, ended!.Status);
        Assert.NotNull(ended.EndedAtUtc);
        Assert.Equal("Customer disconnected", ended.EndReason);
    }

    [Fact]
    public async Task AddSessionEvent_AddsEvent()
    {
        var session = await _service.CreateSessionAsync(
            Guid.NewGuid(), "device-001", null, null);

        var sessionEvent = await _service.AddSessionEventAsync(
            session.Id, SessionEventType.SessionStarted, "Session started by agent");

        Assert.NotEqual(Guid.Empty, sessionEvent.Id);
        Assert.Equal(SessionEventType.SessionStarted, sessionEvent.EventType);
        Assert.Equal(session.Id, sessionEvent.SessionId);
    }

    [Fact]
    public async Task GetActiveSessions_ReturnsOnlyActive()
    {
        var s1 = await _service.CreateSessionAsync(Guid.NewGuid(), "d1", null, null);
        await _service.UpdateSessionStatusAsync(s1.Id, SessionStatus.Active);

        var s2 = await _service.CreateSessionAsync(Guid.NewGuid(), "d2", null, null);
        await _service.UpdateSessionStatusAsync(s2.Id, SessionStatus.Ended, "done");

        var s3 = await _service.CreateSessionAsync(Guid.NewGuid(), "d3", null, null);

        var active = await _service.GetActiveSessionsAsync();

        Assert.Equal(2, active.Count);
        Assert.All(active, s => Assert.True(s.Status == SessionStatus.Active || s.Status == SessionStatus.Pending));
    }
}
