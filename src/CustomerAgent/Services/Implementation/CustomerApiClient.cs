using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CustomerAgent.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace CustomerAgent.Services.Implementation;

public class CustomerApiClient : ICustomerApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CustomerApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public CustomerApiClient(HttpClient httpClient, ILogger<CustomerApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<SupportCodeResult> GenerateSupportCodeAsync(string deviceIdentifier, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new { DeviceIdentifier = deviceIdentifier };
            var response = await _httpClient.PostAsJsonAsync("api/v1/customeragent/support-code", request, JsonOptions, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<SupportCodeResponseDto>(content, JsonOptions);

                if (result is not null)
                {
                    await GetDeviceTokenAsync(deviceIdentifier, cancellationToken);
                    return new SupportCodeResult
                    {
                        IsSuccess = true,
                        Code = result.Code,
                        ExpiresAtUtc = result.ExpiresAtUtc
                    };
                }
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Failed to generate support code: {Error}", errorContent);
            return new SupportCodeResult { IsSuccess = false, ErrorMessage = "Failed to generate support code." };
        }
        catch (HttpRequestException)
        {
            _logger.LogWarning("Server unavailable when generating support code");
            return new SupportCodeResult { IsSuccess = false, ErrorMessage = "Server is unavailable." };
        }
        catch (TaskCanceledException)
        {
            return new SupportCodeResult { IsSuccess = false, ErrorMessage = "Request timed out." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating support code");
            return new SupportCodeResult { IsSuccess = false, ErrorMessage = "An error occurred." };
        }
    }

    public async Task<ConnectionRequestResult> CheckConnectionRequestAsync(string supportCode, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/v1/customeragent/connection-request?code={supportCode}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<ConnectionRequestDto>(content, JsonOptions);

                if (result is not null)
                {
                    return new ConnectionRequestResult
                    {
                        HasPendingRequest = result.HasPendingRequest,
                        SessionId = result.SessionId,
                        AgentName = result.AgentName,
                        AgentRole = result.AgentRole
                    };
                }
            }

            return new ConnectionRequestResult { HasPendingRequest = false };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking connection request");
            return new ConnectionRequestResult { HasPendingRequest = false, ErrorMessage = "Unable to check for requests." };
        }
    }

    public async Task<bool> AcceptConnectionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "api/v1/customeragent/accept";
            var response = await _httpClient.PostAsJsonAsync(url,
                new { SessionId = sessionId }, JsonOptions, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error accepting connection for session {SessionId}", sessionId);
            return false;
        }
    }

    public async Task<bool> RejectConnectionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "api/v1/customeragent/reject";
            var response = await _httpClient.PostAsJsonAsync(url,
                new { SessionId = sessionId }, JsonOptions, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error rejecting connection for session {SessionId}", sessionId);
            return false;
        }
    }

    private static string GetDeviceIdentifier()
    {
        try { return Environment.MachineName; }
        catch { return "customer-device"; }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _httpClient.PostAsJsonAsync("api/v1/customeragent/disconnect",
                new { }, JsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during disconnect");
        }
    }

    public async Task<string?> GetDeviceTokenAsync(string deviceIdentifier, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new { deviceIdentifier };
            var response = await _httpClient.PostAsJsonAsync("api/v1/customeragent/device-token",
                request, JsonOptions, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("token", out var tokenValue))
                {
                    var token = tokenValue.GetString();
                    if (!string.IsNullOrEmpty(token))
                    {
                        _httpClient.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    }
                    return token;
                }
            }

            _logger.LogWarning("Failed to get device token: {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting device token");
        }

        return null;
    }

    private class SupportCodeResponseDto
    {
        public string Code { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
    }

    private class ConnectionRequestDto
    {
        public bool HasPendingRequest { get; set; }
        public Guid? SessionId { get; set; }
        public string? AgentName { get; set; }
        public string? AgentRole { get; set; }
    }
}
