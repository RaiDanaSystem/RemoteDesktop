using RemoteSupport.Server.Application.DTOs;

namespace RemoteSupport.Server.Application.Interfaces;

public interface IAuthenticationService
{
    Task<LoginResponse> LoginAsync(LoginRequest request, string? ipAddress = null, string? userAgent = null, CancellationToken cancellationToken = default);
    Task<LoginResponse> RefreshTokenAsync(RefreshTokenRequest request, string? ipAddress = null, CancellationToken cancellationToken = default);
    Task LogoutAsync(string? refreshToken, Guid? userId = null, string? ipAddress = null, CancellationToken cancellationToken = default);
    Task<UserInfo> RegisterAsync(RegisterRequest request, string? performedByUserId = null, CancellationToken cancellationToken = default);
    Task RevokeAllTokensAsync(Guid userId, CancellationToken cancellationToken = default);
}
