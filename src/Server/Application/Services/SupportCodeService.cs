using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Server.Application.Interfaces;
using RemoteSupport.Server.Domain.Entities;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Application.Services;

public class SupportCodeService : ISupportCodeService
{
    private readonly IApplicationDbContext _context;

    private const int CodeLength = 8;
    private static readonly TimeSpan CodeValidity = TimeSpan.FromMinutes(60);

    public SupportCodeService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<SupportCode> GenerateCodeAsync(string deviceIdentifier, CancellationToken cancellationToken = default)
    {
        string code;
        do
        {
            code = GenerateRandomCode();
        }
        while (await _context.SupportCodes.AnyAsync(c => c.Code == code, cancellationToken));

        var supportCode = new SupportCode
        {
            Id = Guid.NewGuid(),
            Code = code,
            DeviceIdentifier = deviceIdentifier,
            ExpiresAtUtc = DateTime.UtcNow.Add(CodeValidity),
            IsUsed = false,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.SupportCodes.Add(supportCode);
        await _context.SaveChangesAsync(cancellationToken);
        return supportCode;
    }

    public async Task<SupportCode?> ValidateCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);
        if (string.IsNullOrEmpty(normalized))
            return null;

        var supportCode = await _context.SupportCodes
            .FirstOrDefaultAsync(c => c.Code == normalized, cancellationToken);

        if (supportCode is null || supportCode.ExpiresAtUtc < DateTime.UtcNow)
            return null;

        if (!supportCode.IsUsed)
            return supportCode;

        if (supportCode.UsedBySessionId is Guid sessionId)
        {
            var session = await _context.Sessions
                .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
            if (session is not null &&
                (session.Status == SessionStatus.Pending || session.Status == SessionStatus.Active))
            {
                // Same code can reconnect; InitiateConnection ends the previous session.
                return supportCode;
            }
        }

        supportCode.IsUsed = false;
        supportCode.UsedBySessionId = null;
        await _context.SaveChangesAsync(cancellationToken);
        return supportCode;
    }

    public async Task<SupportCode?> ValidateCodeForPollingAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);
        if (string.IsNullOrEmpty(normalized))
            return null;

        var supportCode = await _context.SupportCodes
            .FirstOrDefaultAsync(c => c.Code == normalized, cancellationToken);

        if (supportCode is null || supportCode.ExpiresAtUtc < DateTime.UtcNow)
            return null;

        return supportCode;
    }

    public async Task<IReadOnlyList<SupportCode>> GetActiveCodesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SupportCodes
            .Where(c => !c.IsUsed && c.ExpiresAtUtc > DateTime.UtcNow)
            .OrderByDescending(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> DeviceHasValidCodeAsync(string deviceIdentifier, CancellationToken cancellationToken = default)
    {
        return await _context.SupportCodes.AnyAsync(
            c => c.DeviceIdentifier == deviceIdentifier && c.ExpiresAtUtc > DateTime.UtcNow,
            cancellationToken);
    }

    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return string.Empty;

        var digits = new string(code.Where(char.IsDigit).ToArray());
        if (digits.Length > 0)
            return digits;

        return code.Trim().ToUpperInvariant();
    }

    private static string GenerateRandomCode()
    {
        Span<byte> bytes = stackalloc byte[4];
        int value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = (int)(BitConverter.ToUInt32(bytes) % 90000000) + 10000000;
        }
        while (value is < 10000000 or > 99999999);

        return value.ToString("D8");
    }
}
