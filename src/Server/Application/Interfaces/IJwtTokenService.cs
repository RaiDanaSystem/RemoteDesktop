using RemoteSupport.Server.Domain.Entities;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Application.Interfaces;

public interface IJwtTokenService
{
    string GenerateAccessToken(User user);
    string GenerateCustomerAccessToken(Guid sessionId, string deviceIdentifier);
    string GenerateDeviceToken(string deviceIdentifier);
    string GenerateRefreshToken();
    string HashToken(string token);
}
