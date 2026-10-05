namespace SupportAgent.Services.Interfaces;

public interface IApiClient
{
    void SetAccessToken(string? token);
    Task<LoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<LoginResult> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task LogoutAsync(string? refreshToken, CancellationToken cancellationToken = default);
    Task<UserInfo?> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<ConnectResult> ConnectAsync(string supportCode, CancellationToken cancellationToken = default);
    Task<SessionStatusResult?> GetSessionStatusAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task DisconnectSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public class LoginResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime AccessTokenExpiresAtUtc { get; set; }
    public DateTime RefreshTokenExpiresAtUtc { get; set; }
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? Role { get; set; }
    public string? DisplayName { get; set; }
    public Guid? UserId { get; set; }
}

public class ConnectResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? SessionId { get; set; }
    public string? CustomerDeviceName { get; set; }
    public string? CustomerOperatingSystem { get; set; }
}

public class SessionStatusResult
{
    public Guid SessionId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? CustomerDeviceName { get; set; }
    public DateTime? StartedAtUtc { get; set; }
}

public class UserInfo
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
}
