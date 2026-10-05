namespace SupportAgent.Services.Interfaces;

public interface ITokenStorage
{
    void SaveTokens(string accessToken, string refreshToken, DateTime accessExpiry, DateTime refreshExpiry, string? username = null);
    (string? AccessToken, string? RefreshToken, DateTime AccessExpiry, DateTime RefreshExpiry, string? Username)? LoadTokens();
    void ClearTokens();
}
