using RemoteSupport.Server.Application.DTOs;

namespace RemoteSupport.Server.Application.Interfaces;

public interface ICustomerAuthService
{
    Task<CustomerAuthResponse> AuthenticateAsync(CustomerAuthRequest request, string? ipAddress = null, CancellationToken cancellationToken = default);
    Task<bool> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);
    Task RevokeTokenAsync(string token, CancellationToken cancellationToken = default);
}
