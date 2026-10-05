using Microsoft.EntityFrameworkCore;
using RemoteSupport.Server.Application.Services;
using RemoteSupport.Server.Domain.Entities;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Tests.Application;

public class TransportServiceTests : IDisposable
{
    private readonly RemoteSupport.Server.Infrastructure.Persistence.ApplicationDbContext _context;
    private readonly TransportService _service;
    private readonly PasswordHasher _passwordHasher = new();

    public TransportServiceTests()
    {
        _context = TestHelpers.CreateInMemoryContext();
        var auditService = new AuditService(_context);
        _service = new TransportService(_context, auditService);
    }

    public void Dispose() => _context.Dispose();

    private async Task<(User Agent, Session Session)> CreateActiveSessionAsync()
    {
        var agent = new User
        {
            Id = Guid.NewGuid(), Username = "agent", Email = "a@t.com",
            PasswordHash = _passwordHasher.HashPassword("Pass1!"),
            Role = UserRole.SupportAgent, IsActive = true, CreatedAtUtc = DateTime.UtcNow
        };
        _context.Users.Add(agent);

        var session = new Session
        {
            Id = Guid.NewGuid(),
            AgentUserId = agent.Id,
            AgentUser = agent,
            CustomerDeviceIdentifier = "device-001",
            Status = SessionStatus.Active,
            StartedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };
        _context.Sessions.Add(session);
        await _context.SaveChangesAsync();

        return (agent, session);
    }

    [Fact]
    public async Task IssueTransportToken_ValidSession_ReturnsToken()
    {
        var (agent, session) = await CreateActiveSessionAsync();

        var result = await _service.IssueTransportTokenAsync(session.Id, agent.Id);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Token);
        Assert.NotNull(result.SessionKey);
        Assert.True(result.ExpiresAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task IssueTransportToken_InvalidSession_ReturnsError()
    {
        var agent = new User
        {
            Id = Guid.NewGuid(), Username = "agent", Email = "a@t.com",
            PasswordHash = _passwordHasher.HashPassword("Pass1!"),
            Role = UserRole.SupportAgent, IsActive = true, CreatedAtUtc = DateTime.UtcNow
        };
        _context.Users.Add(agent);
        await _context.SaveChangesAsync();

        var result = await _service.IssueTransportTokenAsync(Guid.NewGuid(), agent.Id);

        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueTransportToken_WrongAgent_ReturnsError()
    {
        var (agent, session) = await CreateActiveSessionAsync();
        var otherAgent = new User
        {
            Id = Guid.NewGuid(), Username = "other", Email = "o@t.com",
            PasswordHash = _passwordHasher.HashPassword("Pass1!"),
            Role = UserRole.SupportAgent, IsActive = true, CreatedAtUtc = DateTime.UtcNow
        };
        _context.Users.Add(otherAgent);
        await _context.SaveChangesAsync();

        var result = await _service.IssueTransportTokenAsync(session.Id, otherAgent.Id);

        Assert.False(result.IsSuccess);
        Assert.Contains("Unauthorized", result.ErrorMessage);
    }

    [Fact]
    public async Task IssueTransportToken_EndedSession_ReturnsError()
    {
        var (agent, session) = await CreateActiveSessionAsync();
        session.Status = SessionStatus.Ended;
        await _context.SaveChangesAsync();

        var result = await _service.IssueTransportTokenAsync(session.Id, agent.Id);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task IssueTransportToken_InactiveUser_ReturnsError()
    {
        var (agent, session) = await CreateActiveSessionAsync();
        agent.IsActive = false;
        await _context.SaveChangesAsync();

        var result = await _service.IssueTransportTokenAsync(session.Id, agent.Id);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task IssueCustomerTransportToken_ValidSession_ReturnsToken()
    {
        var (_, session) = await CreateActiveSessionAsync();

        var result = await _service.IssueCustomerTransportTokenAsync(session.Id, "device-001");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Token);
        Assert.NotNull(result.SessionKey);
    }

    [Fact]
    public async Task IssueCustomerTransportToken_WrongDevice_ReturnsError()
    {
        var (_, session) = await CreateActiveSessionAsync();

        var result = await _service.IssueCustomerTransportTokenAsync(session.Id, "wrong-device");

        Assert.False(result.IsSuccess);
        Assert.Contains("Unauthorized", result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateTransportToken_ValidToken_ReturnsValid()
    {
        var (agent, session) = await CreateActiveSessionAsync();
        var tokenResult = await _service.IssueTransportTokenAsync(session.Id, agent.Id);

        Assert.True(tokenResult.IsSuccess, $"Token issue failed: {tokenResult.ErrorMessage}");
        Assert.NotNull(tokenResult.Token);
        Assert.Equal(4, tokenResult.Token!.Split('.').Length);

        var validation = await _service.ValidateTransportTokenAsync(tokenResult.Token!);

        Assert.True(validation.IsValid, $"Validation failed: {validation.ErrorMessage}");
        Assert.Equal(session.Id, validation.SessionId);
        Assert.NotNull(validation.SessionKey);
    }

    [Fact]
    public async Task ValidateTransportToken_InvalidToken_ReturnsInvalid()
    {
        var validation = await _service.ValidateTransportTokenAsync("invalid.token.here.hash");

        Assert.False(validation.IsValid);
    }

    [Fact]
    public async Task ValidateTransportToken_TamperedToken_ReturnsInvalid()
    {
        var (agent, session) = await CreateActiveSessionAsync();
        var tokenResult = await _service.IssueTransportTokenAsync(session.Id, agent.Id);

        // Tamper with the token
        var parts = tokenResult.Token!.Split('.');
        parts[0] = Guid.NewGuid().ToString(); // Change session ID
        var tamperedToken = string.Join(".", parts);

        var validation = await _service.ValidateTransportTokenAsync(tamperedToken);

        Assert.False(validation.IsValid);
    }

    [Fact]
    public async Task ValidateTransportToken_ExpiredToken_ReturnsInvalid()
    {
        var (agent, session) = await CreateActiveSessionAsync();
        var tokenResult = await _service.IssueTransportTokenAsync(session.Id, agent.Id);

        // Manually create an expired token
        var parts = tokenResult.Token!.Split('.');
        parts[2] = DateTime.UtcNow.AddMinutes(-5).Ticks.ToString(); // Expire it
        var expiredToken = string.Join(".", parts);

        var validation = await _service.ValidateTransportTokenAsync(expiredToken);

        Assert.False(validation.IsValid);
        Assert.Contains("expired", validation.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateTransportToken_EndedSession_ReturnsInvalid()
    {
        var (agent, session) = await CreateActiveSessionAsync();
        var tokenResult = await _service.IssueTransportTokenAsync(session.Id, agent.Id);

        // End the session
        session.Status = SessionStatus.Ended;
        await _context.SaveChangesAsync();

        var validation = await _service.ValidateTransportTokenAsync(tokenResult.Token!);

        Assert.False(validation.IsValid);
    }

    [Fact]
    public async Task ValidateTransportToken_CustomerToken_ReturnsDeviceIdentifier()
    {
        var (_, session) = await CreateActiveSessionAsync();
        var tokenResult = await _service.IssueCustomerTransportTokenAsync(session.Id, "device-001");

        var validation = await _service.ValidateTransportTokenAsync(tokenResult.Token!);

        Assert.True(validation.IsValid);
        Assert.Equal("device-001", validation.DeviceIdentifier);
        Assert.Null(validation.UserId);
    }

    [Fact]
    public async Task RevokeTransportTokens_RevokesAllTokens()
    {
        var (agent, session) = await CreateActiveSessionAsync();
        await _service.IssueTransportTokenAsync(session.Id, agent.Id);
        await _service.IssueTransportTokenAsync(session.Id, agent.Id);

        await _service.RevokeTransportTokensAsync(session.Id);

        var tokens = await _context.TransportTokens
            .Where(t => t.SessionId == session.Id)
            .ToListAsync();

        Assert.All(tokens, t => Assert.True(t.IsRevoked));
    }

    [Fact]
    public async Task ValidateTransportToken_TamperedHash_ReturnsInvalid()
    {
        var (agent, session) = await CreateActiveSessionAsync();
        var tokenResult = await _service.IssueTransportTokenAsync(session.Id, agent.Id);

        // Tamper with the hash
        var parts = tokenResult.Token!.Split('.');
        parts[3] = Convert.ToBase64String(new byte[32]); // Valid base64 but wrong hash
        var badToken = string.Join(".", parts);

        var validation = await _service.ValidateTransportTokenAsync(badToken);

        Assert.False(validation.IsValid, $"Validation should fail for tampered token: {validation.ErrorMessage}");
    }

    [Fact]
    public async Task FullFlow_Issue_Validate_Transmit()
    {
        var (agent, session) = await CreateActiveSessionAsync();

        // Agent gets token
        var agentToken = await _service.IssueTransportTokenAsync(session.Id, agent.Id);
        Assert.True(agentToken.IsSuccess);

        // Customer gets token
        var customerToken = await _service.IssueCustomerTransportTokenAsync(session.Id, "device-001");
        Assert.True(customerToken.IsSuccess);

        // Both validate
        var agentValidation = await _service.ValidateTransportTokenAsync(agentToken.Token!);
        Assert.True(agentValidation.IsValid);
        Assert.Equal(session.Id, agentValidation.SessionId);

        var customerValidation = await _service.ValidateTransportTokenAsync(customerToken.Token!);
        Assert.True(customerValidation.IsValid);
        Assert.Equal(session.Id, customerValidation.SessionId);

        // Both have session keys
        Assert.NotNull(agentValidation.SessionKey);
        Assert.NotNull(customerValidation.SessionKey);
    }
}
