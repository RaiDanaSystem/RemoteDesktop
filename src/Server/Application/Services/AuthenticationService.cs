using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RemoteSupport.Server.Application.DTOs;
using RemoteSupport.Server.Application.Interfaces;
using RemoteSupport.Server.Domain.Entities;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Application.Services;

public class AuthenticationService : IAuthenticationService
{
    private readonly IApplicationDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IAuditService _auditService;
    private readonly ILoginAttemptTracker _loginAttempts;
    private readonly IConfiguration _configuration;

    public AuthenticationService(
        IApplicationDbContext context,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        IAuditService auditService,
        ILoginAttemptTracker loginAttempts,
        IConfiguration configuration)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _auditService = auditService;
        _loginAttempts = loginAttempts;
        _configuration = configuration;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, string? ipAddress = null, string? userAgent = null, CancellationToken cancellationToken = default)
    {
        if (_loginAttempts.IsLocked(request.Username, ipAddress))
        {
            await Task.Delay(250, cancellationToken);
            throw new UnauthorizedAccessException("Too many failed login attempts. Try again later.");
        }

        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Username == request.Username, cancellationToken);

        if (user is null || !user.IsActive || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            _loginAttempts.RecordFailure(request.Username, ipAddress);
            await Task.Delay(250, cancellationToken);
            await _auditService.LogAsync(
                user?.Id,
                AuditAction.UserLoginFailed,
                "Failed login attempt",
                ipAddress, userAgent, cancellationToken);

            throw new UnauthorizedAccessException("Invalid username or password.");
        }

        _loginAttempts.RecordSuccess(request.Username, ipAddress);

        var accessToken = _jwtTokenService.GenerateAccessToken(user);
        var refreshTokenValue = _jwtTokenService.GenerateRefreshToken();
        var refreshTokenHash = _jwtTokenService.HashToken(refreshTokenValue);

        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = refreshTokenHash,
            DeviceIdentifier = request.DeviceIdentifier,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(GetRefreshDays()),
            IsRevoked = false,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.RefreshTokens.Add(refreshToken);
        user.LastLoginAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        var tokenExpiration = DateTime.UtcNow.AddMinutes(GetAccessMinutes());

        await _auditService.LogAsync(
            user.Id, AuditAction.UserLogin,
            $"User {user.Username} logged in",
            ipAddress, userAgent, cancellationToken);

        return new LoginResponse(
            new AccessTokenResponse(accessToken, tokenExpiration),
            new RefreshTokenResponse(refreshTokenValue, refreshToken.ExpiresAtUtc),
            new UserInfo(user.Id, user.Username, user.Email, user.Role.ToString(), user.DisplayName));
    }

    public async Task<LoginResponse> RefreshTokenAsync(RefreshTokenRequest request, string? ipAddress = null, CancellationToken cancellationToken = default)
    {
        var tokenHash = _jwtTokenService.HashToken(request.RefreshToken);

        var refreshToken = await _context.RefreshTokens
            .Include(rt => rt.User)
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);

        if (refreshToken is null)
        {
            await _auditService.LogAsync(null, AuditAction.SecurityEvent,
                "Refresh token not found", ipAddress, null, cancellationToken);
            throw new UnauthorizedAccessException("Invalid refresh token.");
        }

        if (refreshToken.IsRevoked)
        {
            await _auditService.LogAsync(refreshToken.UserId, AuditAction.SecurityEvent,
                "Attempted to use revoked refresh token - possible token reuse", ipAddress, null, cancellationToken);

            await RevokeAllTokensAsync(refreshToken.UserId, cancellationToken);
            throw new UnauthorizedAccessException("Refresh token has been revoked.");
        }

        if (refreshToken.ExpiresAtUtc < DateTime.UtcNow)
        {
            await _auditService.LogAsync(refreshToken.UserId, AuditAction.SecurityEvent,
                "Attempted to use expired refresh token", ipAddress, null, cancellationToken);
            throw new UnauthorizedAccessException("Refresh token has expired.");
        }

        if (!refreshToken.User.IsActive)
        {
            throw new UnauthorizedAccessException("User account is disabled.");
        }

        var newRefreshTokenValue = _jwtTokenService.GenerateRefreshToken();
        var newRefreshTokenHash = _jwtTokenService.HashToken(newRefreshTokenValue);

        refreshToken.IsRevoked = true;
        refreshToken.RevokedAtUtc = DateTime.UtcNow;
        refreshToken.ReplacedByTokenHash = newRefreshTokenHash;
        refreshToken.RevokedByIp = ipAddress;

        var newRefreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = refreshToken.UserId,
            TokenHash = newRefreshTokenHash,
            DeviceIdentifier = refreshToken.DeviceIdentifier,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(GetRefreshDays()),
            IsRevoked = false,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.RefreshTokens.Add(newRefreshToken);
        await _context.SaveChangesAsync(cancellationToken);

        var accessToken = _jwtTokenService.GenerateAccessToken(refreshToken.User);
        var tokenExpiration = DateTime.UtcNow.AddMinutes(GetAccessMinutes());

        await _auditService.LogAsync(refreshToken.UserId, AuditAction.UserLogin,
            "Token refreshed", ipAddress, null, cancellationToken);

        return new LoginResponse(
            new AccessTokenResponse(accessToken, tokenExpiration),
            new RefreshTokenResponse(newRefreshTokenValue, newRefreshToken.ExpiresAtUtc),
            new UserInfo(refreshToken.User.Id, refreshToken.User.Username, refreshToken.User.Email,
                refreshToken.User.Role.ToString(), refreshToken.User.DisplayName));
    }

    public async Task LogoutAsync(string? refreshToken, Guid? userId = null, string? ipAddress = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(refreshToken))
        {
            var tokenHash = _jwtTokenService.HashToken(refreshToken);
            var storedToken = await _context.RefreshTokens
                .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);

            if (storedToken is not null)
            {
                storedToken.IsRevoked = true;
                storedToken.RevokedAtUtc = DateTime.UtcNow;
                storedToken.RevokedByIp = ipAddress;
                userId = storedToken.UserId;
                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        if (userId.HasValue)
        {
            await RevokeAllTokensAsync(userId.Value, cancellationToken);
            await _auditService.LogAsync(userId, AuditAction.UserLogout,
                "User logged out", ipAddress, null, cancellationToken);
        }
    }

    public async Task<UserInfo> RegisterAsync(RegisterRequest request, string? performedByUserId = null, CancellationToken cancellationToken = default)
    {
        if (await _context.Users.AnyAsync(u => u.Username == request.Username, cancellationToken))
            throw new InvalidOperationException("Username already exists.");

        if (await _context.Users.AnyAsync(u => u.Email == request.Email, cancellationToken))
            throw new InvalidOperationException("Email already exists.");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            Email = request.Email,
            PasswordHash = _passwordHasher.HashPassword(request.Password),
            DisplayName = request.DisplayName,
            Role = UserRole.SupportAgent,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(
            user.Id, AuditAction.UserCreated,
            $"User {user.Username} created",
            null, null, cancellationToken);

        return new UserInfo(user.Id, user.Username, user.Email, user.Role.ToString(), user.DisplayName);
    }

    public async Task RevokeAllTokensAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var tokens = await _context.RefreshTokens
            .Where(rt => rt.UserId == userId && !rt.IsRevoked)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.IsRevoked = true;
            token.RevokedAtUtc = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private double GetAccessMinutes()
        => double.TryParse(_configuration["Jwt:AccessTokenExpirationMinutes"], out var minutes) ? minutes : 30;

    private double GetRefreshDays()
        => double.TryParse(_configuration["Jwt:RefreshTokenExpirationDays"], out var days) ? days : 7;
}
