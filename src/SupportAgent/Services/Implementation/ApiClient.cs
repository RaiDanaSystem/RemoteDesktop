using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SupportAgent.Services.Interfaces;

namespace SupportAgent.Services.Implementation;

public class ApiClient : IApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ApiClient> _logger;
    private string? _accessToken;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ApiClient(HttpClient httpClient, ILogger<ApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public void SetAccessToken(string? token)
    {
        _accessToken = token;
        _httpClient.DefaultRequestHeaders.Authorization =
            string.IsNullOrEmpty(token)
                ? null
                : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new { Username = username, Password = password };
            System.Diagnostics.Debug.WriteLine($"[API] LoginAsync called. BaseAddress={_httpClient.BaseAddress}, Timeout={_httpClient.Timeout}");
            var response = await _httpClient.PostAsJsonAsync("api/v1/auth/login", request, JsonOptions, cancellationToken);
            System.Diagnostics.Debug.WriteLine($"[API] Response: {response.StatusCode} {response.ReasonPhrase}");

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<LoginResponseDto>(content, JsonOptions);

                if (result is not null)
                {
                    _accessToken = result.AccessToken.Token;
                    _httpClient.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

                    return new LoginResult
                    {
                        IsSuccess = true,
                        AccessToken = result.AccessToken.Token,
                        RefreshToken = result.RefreshToken.Token,
                        AccessTokenExpiresAtUtc = result.AccessToken.ExpiresAtUtc,
                        RefreshTokenExpiresAtUtc = result.RefreshToken.ExpiresAtUtc,
                        Username = result.User.Username,
                        Email = result.User.Email,
                        Role = result.User.Role,
                        DisplayName = result.User.DisplayName,
                        UserId = result.User.Id
                    };
                }
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorDto = JsonSerializer.Deserialize<ErrorResponse>(errorContent, JsonOptions);

            _logger.LogWarning("Login failed for user {Username}: {Error}", username, errorDto?.Error ?? "Unknown error");
            return new LoginResult { IsSuccess = false, ErrorMessage = errorDto?.Error ?? "Login failed." };
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[API] HttpRequestException: {ex.Message}, Inner={ex.InnerException?.Message}");
            _logger.LogError(ex, "Network error during login");
            return new LoginResult { IsSuccess = false, ErrorMessage = "Unable to connect to server." };
        }
        catch (TaskCanceledException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[API] TaskCanceledException: {ex.Message}");
            return new LoginResult { IsSuccess = false, ErrorMessage = "Request timed out." };
        }
    }

    public async Task<LoginResult> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new { RefreshToken = refreshToken };
            var response = await _httpClient.PostAsJsonAsync("api/v1/auth/refresh", request, JsonOptions, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<LoginResponseDto>(content, JsonOptions);

                if (result is not null)
                {
                    _accessToken = result.AccessToken.Token;
                    _httpClient.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

                    return new LoginResult
                    {
                        IsSuccess = true,
                        AccessToken = result.AccessToken.Token,
                        RefreshToken = result.RefreshToken.Token,
                        AccessTokenExpiresAtUtc = result.AccessToken.ExpiresAtUtc,
                        RefreshTokenExpiresAtUtc = result.RefreshToken.ExpiresAtUtc,
                        Username = result.User.Username,
                        Email = result.User.Email,
                        Role = result.User.Role,
                        DisplayName = result.User.DisplayName,
                        UserId = result.User.Id
                    };
                }
            }

            return new LoginResult { IsSuccess = false, ErrorMessage = "Token refresh failed." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during token refresh");
            return new LoginResult { IsSuccess = false, ErrorMessage = "Unable to refresh token." };
        }
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new { RefreshToken = refreshToken };
            await _httpClient.PostAsJsonAsync("api/v1/auth/logout", request, JsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during logout");
        }
        finally
        {
            _accessToken = null;
            _httpClient.DefaultRequestHeaders.Authorization = null;
        }
    }

    public async Task<UserInfo?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("api/v1/auth/me", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<UserInfo>(content, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting current user");
        }
        return null;
    }

    public async Task<ConnectResult> ConnectAsync(string supportCode, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new { SupportCode = supportCode };
            var response = await _httpClient.PostAsJsonAsync("api/v1/supportagent/connect", request, JsonOptions, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var dto = JsonSerializer.Deserialize<ConnectResponseDto>(content, JsonOptions);
                if (dto is not null)
                {
                    return new ConnectResult
                    {
                        IsSuccess = true,
                        SessionId = dto.SessionId,
                        CustomerDeviceName = dto.CustomerDeviceName,
                        CustomerOperatingSystem = dto.CustomerOperatingSystem
                    };
                }
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorDto = JsonSerializer.Deserialize<ErrorResponse>(errorContent, JsonOptions);
            return new ConnectResult { IsSuccess = false, ErrorMessage = errorDto?.Error ?? "Connection failed." };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error during connect");
            return new ConnectResult { IsSuccess = false, ErrorMessage = "Unable to connect to server." };
        }
        catch (TaskCanceledException)
        {
            return new ConnectResult { IsSuccess = false, ErrorMessage = "Request timed out." };
        }
    }

    public async Task<SessionStatusResult?> GetSessionStatusAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/v1/supportagent/session/{sessionId}/status", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<SessionStatusResult>(content, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting session status");
        }
        return null;
    }

    public async Task DisconnectSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _httpClient.PostAsJsonAsync(
                $"api/v1/supportagent/session/{sessionId}/terminate",
                new { Reason = "Agent disconnected" },
                JsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disconnecting session");
        }
    }

    private class LoginResponseDto
    {
        public TokenDto AccessToken { get; set; } = new();
        public TokenDto RefreshToken { get; set; } = new();
        public UserDto User { get; set; } = new();
    }

    private class TokenDto
    {
        public string Token { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
    }

    private class UserDto
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
    }

    private class ErrorResponse
    {
        public string? Error { get; set; }
    }

    private class ConnectResponseDto
    {
        public Guid SessionId { get; set; }
        public string? CustomerDeviceName { get; set; }
        public string? CustomerOperatingSystem { get; set; }
        public string? Status { get; set; }
    }
}
