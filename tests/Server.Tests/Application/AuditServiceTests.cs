using RemoteSupport.Server.Application.Services;
using RemoteSupport.Server.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace RemoteSupport.Server.Tests.Application;

public class AuditServiceTests : IDisposable
{
    private readonly RemoteSupport.Server.Infrastructure.Persistence.ApplicationDbContext _context;
    private readonly AuditService _service;

    public AuditServiceTests()
    {
        _context = TestHelpers.CreateInMemoryContext();
        _service = new AuditService(_context);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task LogAsync_CreatesAuditLog()
    {
        var userId = Guid.NewGuid();

        await _service.LogAsync(userId, AuditAction.UserLogin, "User logged in", "127.0.0.1", "TestAgent");

        var logs = await _context.AuditLogs.ToListAsync();
        Assert.Single(logs);
        Assert.Equal(userId, logs[0].UserId);
        Assert.Equal(AuditAction.UserLogin, logs[0].Action);
        Assert.Equal("User logged in", logs[0].Description);
        Assert.Equal("127.0.0.1", logs[0].IpAddress);
    }

    [Fact]
    public async Task LogAsync_CanLogWithoutUser()
    {
        await _service.LogAsync(null, AuditAction.SecurityEvent, "Unauthorized access attempt");

        var logs = await _context.AuditLogs.ToListAsync();
        Assert.Single(logs);
        Assert.Null(logs[0].UserId);
        Assert.Equal(AuditAction.SecurityEvent, logs[0].Action);
    }

    [Fact]
    public async Task LogAsync_MultipleLogsAreCreated()
    {
        await _service.LogAsync(Guid.NewGuid(), AuditAction.UserLogin, "login 1");
        await _service.LogAsync(Guid.NewGuid(), AuditAction.UserLogin, "login 2");
        await _service.LogAsync(Guid.NewGuid(), AuditAction.UserLogout, "logout");

        var logs = await _context.AuditLogs.ToListAsync();
        Assert.Equal(3, logs.Count);
    }
}
