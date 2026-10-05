using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Server.Application.DTOs;
using RemoteSupport.Server.Application.Interfaces;
using RemoteSupport.Server.Domain.Entities;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Application.Services;

public class CustomerAuthService : ICustomerAuthService
{
    private readonly IApplicationDbContext _context;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ISessionService _sessionService;
    private readonly ISupportCodeService _supportCodeService;
    private readonly IAuditService _auditService;

    public CustomerAuthService(
        IApplicationDbContext context,
        IJwtTokenService jwtTokenService,
        ISessionService sessionService,
        ISupportCodeService supportCodeService,
        IAuditService auditService)
    {
        _context = context;
        _jwtTokenService = jwtTokenService;
        _sessionService = sessionService;
        _supportCodeService = supportCodeService;
        _auditService = auditService;
    }

    public async Task<CustomerAuthResponse> AuthenticateAsync(CustomerAuthRequest request, string? ipAddress = null, CancellationToken cancellationToken = default)
    {
        var supportCode = await _supportCodeService.ValidateCodeAsync(request.SupportCode, cancellationToken);

        if (supportCode is null)
        {
            await _auditService.LogAsync(null, AuditAction.UserLoginFailed,
                $"Invalid or expired support code used from device: {request.DeviceIdentifier}",
                ipAddress, null, cancellationToken);
            throw new UnauthorizedAccessException("Invalid or expired support code.");
        }

        if (supportCode.DeviceIdentifier != request.DeviceIdentifier)
        {
            await _auditService.LogAsync(null, AuditAction.SecurityEvent,
                $"Support code used from different device than originally generated",
                ipAddress, null, cancellationToken);
            throw new UnauthorizedAccessException("Support code is not valid for this device.");
        }

        var session = await _sessionService.CreateSessionAsync(
            Guid.Empty,
            request.DeviceIdentifier,
            request.DeviceName,
            request.OperatingSystem,
            cancellationToken);

        session.CustomerDeviceIdentifier = request.DeviceIdentifier;
        await _context.SaveChangesAsync(cancellationToken);

        supportCode.IsUsed = true;
        supportCode.UsedBySessionId = session.Id;
        await _context.SaveChangesAsync(cancellationToken);

        var tokenValue = _jwtTokenService.GenerateCustomerAccessToken(session.Id, request.DeviceIdentifier);
        var tokenHash = _jwtTokenService.HashToken(tokenValue);

        var customerToken = new CustomerSessionToken
        {
            Id = Guid.NewGuid(),
            TokenHash = tokenHash,
            DeviceIdentifier = request.DeviceIdentifier,
            DeviceName = request.DeviceName,
            OperatingSystem = request.OperatingSystem,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(8),
            IsRevoked = false,
            LinkedSessionId = session.Id,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.CustomerSessionTokens.Add(customerToken);
        await _context.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(null, AuditAction.UserLogin,
            $"Customer authenticated with support code, session {session.Id}",
            ipAddress, null, cancellationToken);

        return new CustomerAuthResponse(
            new AccessTokenResponse(tokenValue, customerToken.ExpiresAtUtc),
            new SessionInfo(session.Id, session.Status.ToString()));
    }

    public async Task<bool> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        var handler = new JwtSecurityTokenHandler();
        try
        {
            var jwtToken = handler.ReadJwtToken(token);
            var jti = jwtToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;

            if (string.IsNullOrEmpty(jti))
                return false;

            var tokenHash = _jwtTokenService.HashToken(token);

            var storedToken = await _context.CustomerSessionTokens
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

            if (storedToken is null || storedToken.IsRevoked || storedToken.ExpiresAtUtc < DateTime.UtcNow)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task RevokeTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        var tokenHash = _jwtTokenService.HashToken(token);
        var storedToken = await _context.CustomerSessionTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if (storedToken is not null)
        {
            storedToken.IsRevoked = true;
            storedToken.UpdatedAtUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
