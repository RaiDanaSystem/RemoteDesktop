using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Server.Application.Interfaces;
using RemoteSupport.Server.Domain.Entities;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Application.Services;

public class TransportService : ITransportService
{
    private readonly IApplicationDbContext _context;
    private readonly IAuditService _auditService;
    private const int KeySize = 32; // 256 bits
    private const int TokenExpiryMinutes = 60;

    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("RemoteSupport-Transport-Salt-v1");

    public TransportService(IApplicationDbContext context, IAuditService auditService)
    {
        _context = context;
        _auditService = auditService;
    }

    public async Task<TransportTokenResult> IssueTransportTokenAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        var session = await _context.Sessions
            .Include(s => s.AgentUser)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
            return new TransportTokenResult { IsSuccess = false, ErrorMessage = "Session not found." };

        if (session.AgentUserId != userId)
            return new TransportTokenResult { IsSuccess = false, ErrorMessage = "Unauthorized." };

        if (session.AgentUser is not null && !session.AgentUser.IsActive)
            return new TransportTokenResult { IsSuccess = false, ErrorMessage = "User account is disabled." };

        if (session.Status != SessionStatus.Active && session.Status != SessionStatus.Pending)
            return new TransportTokenResult { IsSuccess = false, ErrorMessage = "Session is not active." };

        return await CreateTransportTokenAsync(sessionId, userId.ToString(), cancellationToken);
    }

    public async Task<TransportTokenResult> IssueCustomerTransportTokenAsync(Guid sessionId, string deviceIdentifier, CancellationToken cancellationToken = default)
    {
        var session = await _context.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
            return new TransportTokenResult { IsSuccess = false, ErrorMessage = "Session not found." };

        if (session.CustomerDeviceIdentifier != deviceIdentifier)
            return new TransportTokenResult { IsSuccess = false, ErrorMessage = "Unauthorized." };

        if (session.Status != SessionStatus.Active && session.Status != SessionStatus.Pending)
            return new TransportTokenResult { IsSuccess = false, ErrorMessage = "Session is not active." };

        return await CreateTransportTokenAsync(sessionId, deviceIdentifier, cancellationToken);
    }

    public async Task<TransportValidationResult> ValidateTransportTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 4)
                return new TransportValidationResult { IsValid = false, ErrorMessage = "Invalid token format." };

            var sessionIdStr = parts[0];
            var identityId = parts[1];
            var expiryStr = parts[2];
            var providedHash = parts[3];

            if (!Guid.TryParse(sessionIdStr, out var sessionId))
                return new TransportValidationResult { IsValid = false, ErrorMessage = "Invalid session ID." };

            if (!long.TryParse(expiryStr, out var expiryTicks))
                return new TransportValidationResult { IsValid = false, ErrorMessage = "Invalid expiry." };

            var expiry = new DateTime(expiryTicks, DateTimeKind.Utc);
            if (expiry < DateTime.UtcNow)
                return new TransportValidationResult { IsValid = false, ErrorMessage = "Token expired." };

            var session = await _context.Sessions
                .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

            if (session is null)
                return new TransportValidationResult { IsValid = false, ErrorMessage = "Session not found." };

            if (session.Status == SessionStatus.Ended)
                return new TransportValidationResult { IsValid = false, ErrorMessage = "Session ended." };

            var expectedHash = ComputeTokenHash(sessionIdStr, identityId, expiryStr);

            if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(providedHash),
                Convert.FromBase64String(expectedHash)))
            {
                await _auditService.LogAsync(null, AuditAction.SecurityEvent,
                    $"Invalid transport token for session {sessionId}",
                    null, null, cancellationToken);
                return new TransportValidationResult { IsValid = false, ErrorMessage = "Invalid token signature." };
            }

            var storedToken = await _context.TransportTokens
                .FirstOrDefaultAsync(t => t.SessionId == sessionId && t.TokenHash == expectedHash, cancellationToken);
            if (storedToken is null || storedToken.IsRevoked || storedToken.ExpiresAtUtc < DateTime.UtcNow)
                return new TransportValidationResult { IsValid = false, ErrorMessage = "Token revoked or not found." };

            var sessionKey = DeriveSessionKey(sessionIdStr, identityId);
            var isAgent = Guid.TryParse(identityId, out var userId);

            return new TransportValidationResult
            {
                IsValid = true,
                SessionId = sessionId,
                UserId = isAgent ? userId : null,
                DeviceIdentifier = isAgent ? null : identityId,
                SessionKey = sessionKey
            };
        }
        catch (Exception)
        {
            return new TransportValidationResult { IsValid = false, ErrorMessage = "Token validation failed." };
        }
    }

    public async Task RevokeTransportTokensAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var tokens = await _context.TransportTokens
            .Where(t => t.SessionId == sessionId && !t.IsRevoked)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.IsRevoked = true;
            token.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<TransportTokenResult> CreateTransportTokenAsync(Guid sessionId, string identityId, CancellationToken cancellationToken)
    {
        var expiry = DateTime.UtcNow.AddMinutes(TokenExpiryMinutes);
        var expiryTicks = expiry.Ticks;
        var expiryStr = expiryTicks.ToString();
        var hash = ComputeTokenHash(sessionId.ToString(), identityId, expiryStr);
        var token = $"{sessionId}.{identityId}.{expiryStr}.{hash}";

        var sessionKey = DeriveSessionKey(sessionId.ToString(), identityId);

        var transportToken = new TransportToken
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            UserId = Guid.TryParse(identityId, out var uid) ? (Guid?)uid : null,
            TokenHash = hash,
            EncryptedKey = sessionKey,
            ExpiresAtUtc = expiry,
            IsRevoked = false,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.TransportTokens.Add(transportToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new TransportTokenResult
        {
            IsSuccess = true,
            Token = token,
            SessionKey = sessionKey,
            ExpiresAtUtc = expiry
        };
    }

    private static string ComputeTokenHash(string sessionId, string identityId, string expiry)
    {
        var data = Encoding.UTF8.GetBytes($"{sessionId}.{identityId}.{expiry}");
        var combined = new byte[Salt.Length + data.Length];
        Buffer.BlockCopy(Salt, 0, combined, 0, Salt.Length);
        Buffer.BlockCopy(data, 0, combined, Salt.Length, data.Length);
        var hash = SHA256.HashData(combined);
        return Convert.ToBase64String(hash);
    }

    private static byte[] DeriveSessionKey(string sessionId, string identityId)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            Encoding.UTF8.GetBytes($"{sessionId}.{identityId}"),
            Salt,
            10000,
            HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }
}
