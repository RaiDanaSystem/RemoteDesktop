using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RemoteSupport.Server.Application.DTOs;
using RemoteSupport.Server.Application.Services;
using RemoteSupport.Server.Domain.Entities;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Tests.Application;

public class AuthenticationServiceTests : IDisposable
{
    private readonly RemoteSupport.Server.Infrastructure.Persistence.ApplicationDbContext _context;
    private readonly AuthenticationService _service;
    private readonly PasswordHasher _passwordHasher;

    public AuthenticationServiceTests()
    {
        _context = TestHelpers.CreateInMemoryContext();
        _passwordHasher = new PasswordHasher();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "TestSecretKeyForDevelopment_12345678901234567890!",
                ["Jwt:Issuer"] = "TestIssuer",
                ["Jwt:Audience"] = "TestAudience",
                ["Jwt:AccessTokenExpirationMinutes"] = "30"
            })
            .Build();

        var jwtService = new JwtTokenService(config);
        var auditService = new AuditService(_context);
        var attempts = new LoginAttemptTracker();

        _service = new AuthenticationService(_context, _passwordHasher, jwtService, auditService, attempts, config);
    }

    public void Dispose() => _context.Dispose();

    private async Task<User> CreateTestUserAsync(string username = "testuser", string password = "TestPass123!", UserRole role = UserRole.SupportAgent)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@test.com",
            PasswordHash = _passwordHasher.HashPassword(password),
            Role = role,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task LoginAsync_ReturnsTokens_WhenValidCredentials()
    {
        await CreateTestUserAsync("admin", "AdminPass123!");

        var result = await _service.LoginAsync(new LoginRequest("admin", "AdminPass123!"));

        Assert.False(string.IsNullOrEmpty(result.AccessToken.Token));
        Assert.False(string.IsNullOrEmpty(result.RefreshToken.Token));
        Assert.Equal("admin", result.User.Username);
        Assert.Equal("SupportAgent", result.User.Role);
    }

    [Fact]
    public async Task LoginAsync_ThrowsUnauthorized_WhenWrongPassword()
    {
        await CreateTestUserAsync("user1", "CorrectPass");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _service.LoginAsync(new LoginRequest("user1", "WrongPass")));
    }

    [Fact]
    public async Task LoginAsync_ThrowsUnauthorized_WhenUserNotFound()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _service.LoginAsync(new LoginRequest("nonexistent", "pass")));
    }

    [Fact]
    public async Task LoginAsync_ThrowsUnauthorized_WhenUserDisabled()
    {
        await CreateTestUserAsync("disabled", "Pass123!");
        var user = await _context.Users.FirstAsync(u => u.Username == "disabled");
        user.IsActive = false;
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _service.LoginAsync(new LoginRequest("disabled", "Pass123!")));
    }

    [Fact]
    public async Task LoginAsync_CreatesRefreshTokenInDatabase()
    {
        await CreateTestUserAsync("tokenuser", "Pass123!");

        await _service.LoginAsync(new LoginRequest("tokenuser", "Pass123!"));

        var tokens = await _context.RefreshTokens
            .Where(t => t.User.Username == "tokenuser")
            .ToListAsync();

        Assert.Single(tokens);
        Assert.False(tokens[0].IsRevoked);
    }

    [Fact]
    public async Task LoginAsync_CreatesAuditLog()
    {
        await CreateTestUserAsync("audituser", "Pass123!");

        await _service.LoginAsync(new LoginRequest("audituser", "Pass123!"));

        var logs = await _context.AuditLogs
            .Where(l => l.Action == AuditAction.UserLogin)
            .ToListAsync();

        Assert.Single(logs);
        Assert.Contains("audituser", logs[0].Description!);
    }

    [Fact]
    public async Task LoginAsync_CreatesFailedAuditLog()
    {
        await CreateTestUserAsync("failuser", "CorrectPass");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _service.LoginAsync(new LoginRequest("failuser", "WrongPass")));

        var logs = await _context.AuditLogs
            .Where(l => l.Action == AuditAction.UserLoginFailed)
            .ToListAsync();

        Assert.Single(logs);
    }

    [Fact]
    public async Task LoginAsync_LocksAfterRepeatedFailures()
    {
        await CreateTestUserAsync("lockuser", "RightPass!");

        for (var i = 0; i < LoginAttemptTracker.MaxFailures; i++)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => _service.LoginAsync(new LoginRequest("lockuser", "Wrong"), "127.0.0.1"));
        }

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _service.LoginAsync(new LoginRequest("lockuser", "RightPass!"), "127.0.0.1"));
        Assert.Contains("Too many", ex.Message);
    }

    [Fact]
    public async Task RefreshTokenAsync_ReturnsNewTokens()
    {
        var user = await CreateTestUserAsync("refuser", "Pass123!");
        var loginResult = await _service.LoginAsync(new LoginRequest("refuser", "Pass123!"));

        var refreshResult = await _service.RefreshTokenAsync(
            new RefreshTokenRequest(loginResult.RefreshToken.Token));

        Assert.False(string.IsNullOrEmpty(refreshResult.AccessToken.Token));
        Assert.False(string.IsNullOrEmpty(refreshResult.RefreshToken.Token));
        Assert.NotEqual(loginResult.RefreshToken.Token, refreshResult.RefreshToken.Token);
    }

    [Fact]
    public async Task RefreshTokenAsync_RevokesOldToken()
    {
        var user = await CreateTestUserAsync("revuser", "Pass123!");
        var loginResult = await _service.LoginAsync(new LoginRequest("revuser", "Pass123!"));

        await _service.RefreshTokenAsync(new RefreshTokenRequest(loginResult.RefreshToken.Token));

        var oldTokenHash = new JwtTokenService(new ConfigurationBuilder().Build()).HashToken(loginResult.RefreshToken.Token);
        // We need the real hash from the service, so let's check the DB
        var oldToken = await _context.RefreshTokens
            .FirstOrDefaultAsync(t => t.UserId == user.Id && t.IsRevoked);

        Assert.NotNull(oldToken);
        Assert.True(oldToken!.IsRevoked);
        Assert.NotNull(oldToken.RevokedAtUtc);
    }

    [Fact]
    public async Task RefreshTokenAsync_ThrowsUnauthorized_WhenTokenRevoked()
    {
        var user = await CreateTestUserAsync("revokeuser", "Pass123!");
        var loginResult = await _service.LoginAsync(new LoginRequest("revokeuser", "Pass123!"));

        // Refresh once (revokes old token)
        await _service.RefreshTokenAsync(new RefreshTokenRequest(loginResult.RefreshToken.Token));

        // Try to use the old token again
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _service.RefreshTokenAsync(new RefreshTokenRequest(loginResult.RefreshToken.Token)));
    }

    [Fact]
    public async Task LogoutAsync_RevokesAllTokens()
    {
        var user = await CreateTestUserAsync("logoutuser", "Pass123!");
        await _service.LoginAsync(new LoginRequest("logoutuser", "Pass123!"));
        await _service.LoginAsync(new LoginRequest("logoutuser", "Pass123!"));

        await _service.LogoutAsync(null, user.Id);

        var activeTokens = await _context.RefreshTokens
            .Where(t => t.UserId == user.Id && !t.IsRevoked)
            .ToListAsync();

        Assert.Empty(activeTokens);
    }

    [Fact]
    public async Task RegisterAsync_CreatesNewUser()
    {
        var result = await _service.RegisterAsync(
            new RegisterRequest("newuser", "new@test.com", "NewPass123!"));

        Assert.Equal("newuser", result.Username);
        Assert.Equal("new@test.com", result.Email);

        var user = await _context.Users.FirstAsync(u => u.Username == "newuser");
        Assert.True(_passwordHasher.VerifyPassword("NewPass123!", user.PasswordHash));
    }

    [Fact]
    public async Task RegisterAsync_ThrowsOnDuplicateUsername()
    {
        await CreateTestUserAsync("dupuser", "Pass123!");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RegisterAsync(new RegisterRequest("dupuser", "other@test.com", "Pass123!")));
    }

    [Fact]
    public async Task RegisterAsync_ThrowsOnDuplicateEmail()
    {
        await CreateTestUserAsync("usera", "Pass123!");
        var existingEmail = (await _context.Users.FirstAsync(u => u.Username == "usera")).Email;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RegisterAsync(new RegisterRequest("userb", existingEmail, "Pass123!")));
    }
}
