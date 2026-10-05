using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using CustomerAgent.Services.Interfaces;

namespace CustomerAgent.Services.Implementation;

public class CustomerSignalRClient : ISignalRClient
{
    private HubConnection? _connection;
    private readonly ILogger<CustomerSignalRClient> _logger;
    private string _deviceIdentifier = string.Empty;
    private string? _accessToken;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public event Action<Guid, string, string?, string, DateTime>? ConnectionRequestReceived;
    public event Action<Guid, string, string?, DateTime?, string?>? SessionStateChanged;
    public event Action<Guid, string, string?, DateTime?>? SessionTerminated;
    public event Action<string>? ConnectionError;
    public event Action? Connected;
    public event Action? Disconnected;

    public CustomerSignalRClient(ILogger<CustomerSignalRClient> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(string serverUrl, string deviceIdentifier, CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _deviceIdentifier = deviceIdentifier;

        var token = await GetTokenFromServerAsync(serverUrl, deviceIdentifier, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            ConnectionError?.Invoke("Failed to obtain authentication token from server.");
            return;
        }

        _accessToken = token;

        var hubUrl = $"{serverUrl.TrimEnd('/')}/hubs/session";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(_accessToken)!;
            })
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        _connection.Closed += async error =>
        {
            _logger.LogWarning("Customer SignalR closed: {Error}", error?.Message);
            Disconnected?.Invoke();
        };

        _connection.Reconnected += connectionId =>
        {
            _logger.LogInformation("Customer SignalR reconnected: {ConnectionId}", connectionId);
            Connected?.Invoke();
            return Task.CompletedTask;
        };

        RegisterHandlers(_connection);

        try
        {
            await _connection.StartAsync(cancellationToken);
            Connected?.Invoke();
            _logger.LogInformation("Customer SignalR connected");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect Customer SignalR");
            ConnectionError?.Invoke($"Connection failed: {ex.Message}");
        }
    }

    private async Task<string?> GetTokenFromServerAsync(string serverUrl, string deviceIdentifier, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient();
            var url = $"{serverUrl.TrimEnd('/')}/api/v1/customeragent/device-token";

            var request = new { deviceIdentifier };
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(url, content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("token", out var tokenValue))
                {
                    return tokenValue.GetString();
                }
            }

            _logger.LogWarning("Failed to get device token from server: {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting device token from server");
        }

        return null;
    }

    private void RegisterHandlers(HubConnection connection)
    {
        connection.On("ConnectionRequestCreated", (JsonElement data) =>
        {
            try
            {
                var sessionId = data.GetProperty("sessionId").GetGuid();
                var agentName = data.TryGetProperty("agentName", out var an) ? an.GetString() : null;
                var agentDisplayName = data.TryGetProperty("agentDisplayName", out var ad) ? ad.GetString() : null;
                var agentRole = data.TryGetProperty("agentRole", out var ar) ? ar.GetString() ?? "" : "";
                var requestedAt = data.TryGetProperty("requestedAtUtc", out var ra) ? ra.GetDateTime() : DateTime.UtcNow;
                ConnectionRequestReceived?.Invoke(sessionId, agentName ?? "", agentDisplayName, agentRole, requestedAt);
            }
            catch (Exception ex) { _logger.LogError(ex, "Error parsing ConnectionRequestCreated"); }
        });

        connection.On("SessionTerminated", (JsonElement data) =>
        {
            try
            {
                var sessionId = data.GetProperty("sessionId").GetGuid();
                var status = data.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                var endReason = data.TryGetProperty("endReason", out var er) ? er.GetString() : null;
                var endedAt = data.TryGetProperty("endedAtUtc", out var ea) && ea.ValueKind != JsonValueKind.Null ? ea.GetDateTime() : (DateTime?)null;
                SessionTerminated?.Invoke(sessionId, status, endReason, endedAt);
            }
            catch (Exception ex) { _logger.LogError(ex, "Error parsing SessionTerminated"); }
        });

        connection.On("SessionStatusChanged", (JsonElement data) =>
        {
            try
            {
                var sessionId = data.GetProperty("sessionId").GetGuid();
                var status = data.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                var agentName = data.TryGetProperty("agentName", out var an) ? an.GetString() : null;
                var startedAt = data.TryGetProperty("startedAtUtc", out var sa) && sa.ValueKind != JsonValueKind.Null ? sa.GetDateTime() : (DateTime?)null;
                var endReason = data.TryGetProperty("endReason", out var er) ? er.GetString() : null;
                SessionStateChanged?.Invoke(sessionId, status, agentName, startedAt, endReason);
            }
            catch (Exception ex) { _logger.LogError(ex, "Error parsing SessionStatusChanged"); }
        });

        connection.On("ConnectionError", (JsonElement data) =>
        {
            try
            {
                var msg = data.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                ConnectionError?.Invoke(msg ?? "Unknown error");
            }
            catch (Exception ex) { _logger.LogError(ex, "Error parsing ConnectionError"); }
        });

        connection.On("Heartbeat", () => { });
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            await _connection.StopAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
