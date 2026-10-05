using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RemoteSupport.Server.Application.Services;
using RemoteSupport.Server.Domain.Entities;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Tests.Application;

public class SessionManagerTests : IDisposable
{
    private readonly RemoteSupport.Server.Infrastructure.Persistence.ApplicationDbContext _context;
    private readonly SessionManager _sessionManager;
    private readonly PasswordHasher _passwordHasher = new();

    public SessionManagerTests()
    {
        _context = TestHelpers.CreateInMemoryContext();

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "TestSecretKeyForDevelopment_12345678901234567890!",
                ["Jwt:Issuer"] = "TestIssuer",
                ["Jwt:Audience"] = "TestAudience",
                ["Jwt:AccessTokenExpirationMinutes"] = "30"
            })
            .Build();

        var sessionService = new SessionService(_context);
        var supportCodeService = new SupportCodeService(_context);
        var auditService = new AuditService(_context);

        _sessionManager = new SessionManager(_context, sessionService, supportCodeService, auditService);
    }

    public void Dispose() => _context.Dispose();

    private async Task<User> CreateAgentAsync(string username = "agent1")
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@test.com",
            PasswordHash = _passwordHasher.HashPassword("Pass123!"),
            Role = UserRole.SupportAgent,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private async Task<SupportCode> CreateValidCodeAsync(string deviceIdentifier = "customer-device-001")
    {
        var code = await new SupportCodeService(_context).GenerateCodeAsync(deviceIdentifier);
        return code;
    }

    [Fact]
    public async Task InitiateConnection_ValidCode_CreatesSession()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync();

        var result = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.SessionId);

        var session = await _context.Sessions.FindAsync(result.SessionId!.Value);
        Assert.NotNull(session);
        Assert.Equal(SessionStatus.Pending, session!.Status);
        Assert.Equal(agent.Id, session.AgentUserId);
    }

    [Fact]
    public async Task InitiateConnection_InvalidCode_ReturnsError()
    {
        var agent = await CreateAgentAsync();

        var result = await _sessionManager.InitiateConnectionAsync(agent.Id, "INVALID");

        Assert.False(result.IsSuccess);
        Assert.Contains("Invalid", result.ErrorMessage);
    }

    [Fact]
    public async Task InitiateConnection_ExpiredCode_ReturnsError()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync();

        // Expire the code
        var storedCode = await _context.SupportCodes.FirstAsync(c => c.Code == code.Code);
        storedCode.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await _context.SaveChangesAsync();

        var result = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        Assert.False(result.IsSuccess);
        Assert.Contains("expired", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitiateConnection_UsedCode_ReturnsError()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync();

        // Use the code
        var storedCode = await _context.SupportCodes.FirstAsync(c => c.Code == code.Code);
        storedCode.IsUsed = true;
        await _context.SaveChangesAsync();

        var result = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task InitiateConnection_InactiveUser_ReturnsError()
    {
        var agent = await CreateAgentAsync();
        agent.IsActive = false;
        await _context.SaveChangesAsync();
        var code = await CreateValidCodeAsync();

        var result = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task InitiateConnection_CodeMarkedAsUsed()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync();

        await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        var storedCode = await _context.SupportCodes.FirstAsync(c => c.Code == code.Code);
        Assert.True(storedCode.IsUsed);
    }

    [Fact]
    public async Task InitiateConnection_CreatesAuditLog()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync();

        await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        var logs = await _context.AuditLogs
            .Where(l => l.Action == AuditAction.SessionCreated)
            .ToListAsync();

        Assert.Single(logs);
    }

    [Fact]
    public async Task InitiateConnection_InvalidCode_CreatesFailedAuditLog()
    {
        var agent = await CreateAgentAsync();

        await _sessionManager.InitiateConnectionAsync(agent.Id, "INVALID");

        var logs = await _context.AuditLogs
            .Where(l => l.Action == AuditAction.SessionCreated)
            .ToListAsync();

        Assert.Single(logs);
        Assert.Contains("invalid", logs[0].Description!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPendingConnectionRequest_NoPending_ReturnsEmpty()
    {
        var result = await _sessionManager.GetPendingConnectionRequestAsync("device-001");

        Assert.False(result.HasPendingRequest);
    }

    [Fact]
    public async Task GetPendingConnectionRequest_PendingSession_ReturnsRequest()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");

        await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        var result = await _sessionManager.GetPendingConnectionRequestAsync("device-001");

        Assert.True(result.HasPendingRequest);
        Assert.NotNull(result.SessionId);
        Assert.Equal("agent1", result.AgentName);
    }

    [Fact]
    public async Task AcceptConnection_ValidSession_ActivatesSession()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        var result = await _sessionManager.AcceptConnectionAsync(
            initResult.SessionId!.Value, "device-001");

        Assert.True(result.IsSuccess);

        var session = await _context.Sessions.FindAsync(initResult.SessionId!.Value);
        Assert.Equal(SessionStatus.Active, session!.Status);
        Assert.NotNull(session.StartedAtUtc);
    }

    [Fact]
    public async Task AcceptConnection_WrongDevice_ReturnsUnauthorized()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        var result = await _sessionManager.AcceptConnectionAsync(
            initResult.SessionId!.Value, "wrong-device");

        Assert.False(result.IsSuccess);
        Assert.Contains("Unauthorized", result.ErrorMessage);
    }

    [Fact]
    public async Task AcceptConnection_NonexistentSession_ReturnsError()
    {
        var result = await _sessionManager.AcceptConnectionAsync(Guid.NewGuid(), "device-001");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task AcceptConnection_WrongDevice_CreatesSecurityAuditLog()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        await _sessionManager.AcceptConnectionAsync(initResult.SessionId!.Value, "wrong-device");

        var logs = await _context.AuditLogs
            .Where(l => l.Action == AuditAction.SecurityEvent)
            .ToListAsync();

        Assert.NotEmpty(logs);
    }

    [Fact]
    public async Task AcceptConnection_CreatesSessionEvent()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        await _sessionManager.AcceptConnectionAsync(initResult.SessionId!.Value, "device-001");

        var events = await _context.SessionEvents
            .Where(e => e.SessionId == initResult.SessionId!.Value)
            .ToListAsync();

        Assert.Contains(events, e => e.EventType == SessionEventType.ConnectionAccepted);
    }

    [Fact]
    public async Task RejectConnection_ValidSession_EndsSession()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        var result = await _sessionManager.RejectConnectionAsync(
            initResult.SessionId!.Value, "device-001");

        Assert.True(result.IsSuccess);

        var session = await _context.Sessions.FindAsync(initResult.SessionId!.Value);
        Assert.Equal(SessionStatus.Ended, session!.Status);
        Assert.Contains("rejected", session.EndReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectConnection_WrongDevice_ReturnsUnauthorized()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        var result = await _sessionManager.RejectConnectionAsync(
            initResult.SessionId!.Value, "wrong-device");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task RejectConnection_CreatesAuditLog()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        await _sessionManager.RejectConnectionAsync(initResult.SessionId!.Value, "device-001");

        var logs = await _context.AuditLogs
            .Where(l => l.Action == AuditAction.SessionDisconnected)
            .ToListAsync();

        Assert.Single(logs);
    }

    [Fact]
    public async Task TerminateSession_ByAgent_EndsSession()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);
        await _sessionManager.AcceptConnectionAsync(initResult.SessionId!.Value, "device-001");

        var result = await _sessionManager.TerminateSessionAsync(
            initResult.SessionId!.Value, agent.Id, null, "Agent ended");

        Assert.True(result.IsSuccess);

        var session = await _context.Sessions.FindAsync(initResult.SessionId!.Value);
        Assert.Equal(SessionStatus.Ended, session!.Status);
        Assert.Contains("Agent ended", session.EndReason!);
    }

    [Fact]
    public async Task TerminateSession_ByCustomer_EndsSession()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);
        await _sessionManager.AcceptConnectionAsync(initResult.SessionId!.Value, "device-001");

        var result = await _sessionManager.TerminateSessionAsync(
            initResult.SessionId!.Value, null, "device-001", "Customer ended");

        Assert.True(result.IsSuccess);

        var session = await _context.Sessions.FindAsync(initResult.SessionId!.Value);
        Assert.Equal(SessionStatus.Ended, session!.Status);
    }

    [Fact]
    public async Task TerminateSession_UnauthorizedUser_ReturnsError()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);
        await _sessionManager.AcceptConnectionAsync(initResult.SessionId!.Value, "device-001");

        var result = await _sessionManager.TerminateSessionAsync(
            initResult.SessionId!.Value, Guid.NewGuid(), null, null);

        Assert.False(result.IsSuccess);
        Assert.Contains("Unauthorized", result.ErrorMessage);
    }

    [Fact]
    public async Task TerminateSession_AlreadyEnded_ReturnsError()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);
        await _sessionManager.AcceptConnectionAsync(initResult.SessionId!.Value, "device-001");

        await _sessionManager.TerminateSessionAsync(initResult.SessionId!.Value, agent.Id, null, "First end");

        var result = await _sessionManager.TerminateSessionAsync(
            initResult.SessionId!.Value, agent.Id, null, "Second end");

        Assert.False(result.IsSuccess);
        Assert.Contains("already ended", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TerminateSession_NonexistentSession_ReturnsError()
    {
        var result = await _sessionManager.TerminateSessionAsync(
            Guid.NewGuid(), Guid.NewGuid(), null, null);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task TerminateSession_CreatesAuditLog()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);
        await _sessionManager.AcceptConnectionAsync(initResult.SessionId!.Value, "device-001");

        await _sessionManager.TerminateSessionAsync(initResult.SessionId!.Value, agent.Id, null, "Test");

        var logs = await _context.AuditLogs
            .Where(l => l.Action == AuditAction.SessionDisconnected)
            .ToListAsync();

        Assert.NotEmpty(logs);
    }

    [Fact]
    public async Task GetSessionStatus_ForAgent_ReturnsCorrectStatus()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        var status = await _sessionManager.GetSessionStatusAsync(
            initResult.SessionId!.Value, agent.Id, null);

        Assert.True(status.SessionExists);
        Assert.Equal("Pending", status.Status);
        Assert.Equal("agent1", status.AgentName);
    }

    [Fact]
    public async Task GetSessionStatus_ForWrongAgent_ReturnsNotFound()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        var status = await _sessionManager.GetSessionStatusAsync(
            initResult.SessionId!.Value, Guid.NewGuid(), null);

        Assert.False(status.SessionExists);
    }

    [Fact]
    public async Task GetSessionStatus_ForCustomer_ReturnsCorrectStatus()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);

        var status = await _sessionManager.GetSessionStatusAsync(
            initResult.SessionId!.Value, null, "device-001");

        Assert.True(status.SessionExists);
        Assert.Equal("Pending", status.Status);
    }

    [Fact]
    public async Task InitiateConnection_DuplicateActiveSession_ReturnsError()
    {
        var agent = await CreateAgentAsync();
        var code1 = await CreateValidCodeAsync("device-001");
        await _sessionManager.InitiateConnectionAsync(agent.Id, code1.Code);

        var code2 = await CreateValidCodeAsync("device-001");
        var result = await _sessionManager.InitiateConnectionAsync(agent.Id, code2.Code);

        Assert.False(result.IsSuccess);
        Assert.Contains("active or pending", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullFlow_ValidConnection_Accept_Terminate()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("customer-pc");

        // Step 1: Agent initiates
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);
        Assert.True(initResult.IsSuccess);

        // Step 2: Customer polls and finds request
        var pollResult = await _sessionManager.GetPendingConnectionRequestAsync("customer-pc");
        Assert.True(pollResult.HasPendingRequest);
        Assert.Equal("agent1", pollResult.AgentName);

        // Step 3: Customer accepts
        var acceptResult = await _sessionManager.AcceptConnectionAsync(
            initResult.SessionId!.Value, "customer-pc");
        Assert.True(acceptResult.IsSuccess);

        // Step 4: Both check status
        var agentStatus = await _sessionManager.GetSessionStatusAsync(
            initResult.SessionId!.Value, agent.Id, null);
        Assert.Equal("Active", agentStatus.Status);

        var customerStatus = await _sessionManager.GetSessionStatusAsync(
            initResult.SessionId!.Value, null, "customer-pc");
        Assert.Equal("Active", customerStatus.Status);

        // Step 5: Customer terminates
        var termResult = await _sessionManager.TerminateSessionAsync(
            initResult.SessionId!.Value, null, "customer-pc", "Done");
        Assert.True(termResult.IsSuccess);

        // Step 6: Verify ended
        var finalStatus = await _sessionManager.GetSessionStatusAsync(
            initResult.SessionId!.Value, agent.Id, null);
        Assert.Equal("Ended", finalStatus.Status);
    }

    [Fact]
    public async Task FullFlow_ValidConnection_Reject()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("customer-pc");

        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);
        Assert.True(initResult.IsSuccess);

        var pollResult = await _sessionManager.GetPendingConnectionRequestAsync("customer-pc");
        Assert.True(pollResult.HasPendingRequest);

        var rejectResult = await _sessionManager.RejectConnectionAsync(
            initResult.SessionId!.Value, "customer-pc");
        Assert.True(rejectResult.IsSuccess);

        var status = await _sessionManager.GetSessionStatusAsync(
            initResult.SessionId!.Value, agent.Id, null);
        Assert.Equal("Ended", status.Status);
    }

    [Fact]
    public async Task InitiateConnection_ExistingActiveSession_BlocksWithoutCleanup()
    {
        var agent = await CreateAgentAsync();
        var code1 = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code1.Code);
        await _sessionManager.AcceptConnectionAsync(initResult.SessionId!.Value, "device-001");

        var code2 = await CreateValidCodeAsync("device-001");
        var result = await _sessionManager.InitiateConnectionAsync(agent.Id, code2.Code);

        Assert.False(result.IsSuccess);
        Assert.Contains("active or pending", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitiateConnection_ExistingPendingSession_BlocksWithoutCleanup()
    {
        var agent = await CreateAgentAsync();
        var code1 = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code1.Code);

        var code2 = await CreateValidCodeAsync("device-001");
        var result = await _sessionManager.InitiateConnectionAsync(agent.Id, code2.Code);

        Assert.False(result.IsSuccess);
        Assert.Contains("active or pending", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitiateConnection_TerminatedSession_AllowsNewConnection()
    {
        var agent = await CreateAgentAsync();
        var code1 = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code1.Code);
        await _sessionManager.AcceptConnectionAsync(initResult.SessionId!.Value, "device-001");
        await _sessionManager.TerminateSessionAsync(initResult.SessionId!.Value, agent.Id, null, "Normal end");

        var code2 = await CreateValidCodeAsync("device-001");
        var result = await _sessionManager.InitiateConnectionAsync(agent.Id, code2.Code);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.SessionId);
    }

    [Fact]
    public async Task InitiateConnection_StalePendingSession_AutoCleanedAndAllows()
    {
        var agent = await CreateAgentAsync();
        var code1 = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code1.Code);

        // Make session stale (>10 minutes old)
        var session = await _context.Sessions.FirstAsync(s => s.Id == initResult.SessionId);
        session.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-15);
        session.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-15);
        await _context.SaveChangesAsync();

        var code2 = await CreateValidCodeAsync("device-001");
        var result = await _sessionManager.InitiateConnectionAsync(agent.Id, code2.Code);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.SessionId);

        var endedSession = await _context.Sessions.FindAsync(initResult.SessionId.Value);
        Assert.Equal(SessionStatus.Ended, endedSession!.Status);
        Assert.Contains("auto-terminated", endedSession.EndReason!);
    }

    [Fact]
    public async Task InitiateConnection_StaleActiveSession_AutoCleanedAndAllows()
    {
        var agent = await CreateAgentAsync();
        var code1 = await CreateValidCodeAsync("device-001");
        var initResult = await _sessionManager.InitiateConnectionAsync(agent.Id, code1.Code);
        await _sessionManager.AcceptConnectionAsync(initResult.SessionId!.Value, "device-001");

        // Make session stale (>30 minutes old)
        var session = await _context.Sessions.FirstAsync(s => s.Id == initResult.SessionId);
        session.StartedAtUtc = DateTime.UtcNow.AddMinutes(-35);
        session.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-35);
        await _context.SaveChangesAsync();

        var code2 = await CreateValidCodeAsync("device-001");
        var result = await _sessionManager.InitiateConnectionAsync(agent.Id, code2.Code);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.SessionId);

        var endedSession = await _context.Sessions.FindAsync(initResult.SessionId.Value);
        Assert.Equal(SessionStatus.Ended, endedSession!.Status);
        Assert.Contains("auto-terminated", endedSession.EndReason!);
    }

    [Fact]
    public async Task CleanupStaleSessionsAsync_EndsExpiredPendingAndActive()
    {
        // Create two devices with one stale pending and one stale active
        var deviceA = await CreateAgentAsync();
        var code1 = await CreateValidCodeAsync("device-a");
        var s1 = await _sessionManager.InitiateConnectionAsync(deviceA.Id, code1.Code);
        await _sessionManager.AcceptConnectionAsync(s1.SessionId!.Value, "device-a");

        var deviceB = await CreateAgentAsync();
        var code2 = await CreateValidCodeAsync("device-b");
        await _sessionManager.InitiateConnectionAsync(deviceB.Id, code2.Code);

        // Make pending session stale (>10 min)
        var pendingSession = await _context.Sessions.FirstAsync(s => s.Status == SessionStatus.Pending);
        pendingSession.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20);
        await _context.SaveChangesAsync();

        // Make active session stale (>30 min)
        var activeSession = await _context.Sessions.FirstAsync(s => s.Status == SessionStatus.Active);
        activeSession.StartedAtUtc = DateTime.UtcNow.AddMinutes(-40);
        activeSession.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-40);
        await _context.SaveChangesAsync();

        int pendingCount = await _sessionManager.CleanupPendingSessionsAsync(TimeSpan.FromMinutes(10));
        int activeCount = await _sessionManager.CleanupActiveSessionsAsync(TimeSpan.FromMinutes(30));

        Assert.Equal(1, pendingCount);
        Assert.Equal(1, activeCount);

        var pendingEnded = await _context.Sessions.FindAsync(pendingSession.Id);
        var activeEnded = await _context.Sessions.FindAsync(activeSession.Id);
        Assert.Equal(SessionStatus.Ended, pendingEnded!.Status);
        Assert.Equal(SessionStatus.Ended, activeEnded!.Status);
    }

    [Fact]
    public async Task FullFlow_ClosedCustomerNoTerminate_SessionStillUsableByDifferentAgent()
    {
        // Simulates customer crashing without terminating
        var agent1 = await CreateAgentAsync("agent1");
        var code1 = await CreateValidCodeAsync("crash-device");
        var init1 = await _sessionManager.InitiateConnectionAsync(agent1.Id, code1.Code);
        await _sessionManager.AcceptConnectionAsync(init1.SessionId!.Value, "crash-device");

        // No terminate called — simulate crash by making session stale
        var session = await _context.Sessions.FirstAsync(s => s.Id == init1.SessionId);
        session.StartedAtUtc = DateTime.UtcNow.AddMinutes(-35);
        session.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-35);
        await _context.SaveChangesAsync();

        var agent2 = await CreateAgentAsync("agent2");
        var code2 = await CreateValidCodeAsync("crash-device");
        var result = await _sessionManager.InitiateConnectionAsync(agent2.Id, code2.Code);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.SessionId);
        Assert.NotEqual(init1.SessionId, result.SessionId);
    }

    [Fact]
    public async Task SameCode_Reconnects_AfterDeviceSessionsTerminated()
    {
        var agent = await CreateAgentAsync();
        var code = await CreateValidCodeAsync("device-001");
        var first = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);
        await _sessionManager.AcceptConnectionAsync(first.SessionId!.Value, "device-001");

        var ended = await _sessionManager.TerminateOpenSessionsForDeviceAsync(
            "device-001", "Customer disconnected");
        Assert.Equal(1, ended);

        var second = await _sessionManager.InitiateConnectionAsync(agent.Id, code.Code);
        Assert.True(second.IsSuccess);
        Assert.NotNull(second.SessionId);
        Assert.NotEqual(first.SessionId, second.SessionId);
    }
}
