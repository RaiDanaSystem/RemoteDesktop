using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using SupportAgent.Services.Interfaces;

namespace SupportAgent.Services.Implementation;

public class SupportSignalRClient : ISignalRClient
{
    private HubConnection? _connection;
    private readonly ILogger<SupportSignalRClient> _logger;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public event Action<SessionRequestReceivedEventArgs>? SessionRequestReceived;
    public event Action<SessionStateChangedEventArgs>? SessionStateChanged;
    public event Action<SessionTerminatedEventArgs>? SessionTerminated;
    public event Action<string>? ConnectionError;
    public event Action? Connected;
    public event Action? Disconnected;

    public SupportSignalRClient(ILogger<SupportSignalRClient> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(string serverUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        var hubUrl = $"{serverUrl.TrimEnd('/')}/hubs/session";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(accessToken)!;
            })
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        _connection.Closed += async error =>
        {
            _logger.LogWarning("SignalR connection closed: {Error}", error?.Message);
            Disconnected?.Invoke();
        };

        _connection.Reconnecting += error =>
        {
            _logger.LogInformation("SignalR reconnecting...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += connectionId =>
        {
            _logger.LogInformation("SignalR reconnected: {ConnectionId}", connectionId);
            Connected?.Invoke();
            return Task.CompletedTask;
        };

        _connection.On("ConnectionRequestCreated", (JsonElement data) =>
        {
            try
            {
                var evt = JsonSerializer.Deserialize<SessionRequestReceivedEventArgs>(data.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (evt is not null) SessionRequestReceived?.Invoke(evt);
            }
            catch (Exception ex) { _logger.LogError(ex, "Error parsing ConnectionRequestCreated"); }
        });

        _connection.On("ConnectionRequestAccepted", (JsonElement data) =>
        {
            try
            {
                var evt = JsonSerializer.Deserialize<SessionStateChangedEventArgs>(data.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (evt is not null) SessionStateChanged?.Invoke(evt);
            }
            catch (Exception ex) { _logger.LogError(ex, "Error parsing ConnectionRequestAccepted"); }
        });

        _connection.On("ConnectionRequestRejected", (JsonElement data) =>
        {
            try
            {
                var evt = JsonSerializer.Deserialize<SessionStateChangedEventArgs>(data.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (evt is not null) SessionStateChanged?.Invoke(evt);
            }
            catch (Exception ex) { _logger.LogError(ex, "Error parsing ConnectionRequestRejected"); }
        });

        _connection.On("SessionStarted", (JsonElement data) =>
        {
            try
            {
                var evt = JsonSerializer.Deserialize<SessionStateChangedEventArgs>(data.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (evt is not null) SessionStateChanged?.Invoke(evt);
            }
            catch (Exception ex) { _logger.LogError(ex, "Error parsing SessionStarted"); }
        });

        _connection.On("SessionTerminated", (JsonElement data) =>
        {
            try
            {
                var evt = JsonSerializer.Deserialize<SessionTerminatedEventArgs>(data.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (evt is not null) SessionTerminated?.Invoke(evt);
            }
            catch (Exception ex) { _logger.LogError(ex, "Error parsing SessionTerminated"); }
        });

        _connection.On("SessionStatusChanged", (JsonElement data) =>
        {
            try
            {
                var evt = JsonSerializer.Deserialize<SessionStateChangedEventArgs>(data.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (evt is not null) SessionStateChanged?.Invoke(evt);
            }
            catch (Exception ex) { _logger.LogError(ex, "Error parsing SessionStatusChanged"); }
        });

        _connection.On("ConnectionError", (JsonElement data) =>
        {
            try
            {
                var msg = data.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                ConnectionError?.Invoke(msg ?? "Unknown error");
            }
            catch (Exception ex) { _logger.LogError(ex, "Error parsing ConnectionError"); }
        });

        _connection.On("Heartbeat", () => { });

        try
        {
            await _connection.StartAsync(cancellationToken);
            Connected?.Invoke();
            _logger.LogInformation("SignalR connected to {Url}", hubUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect SignalR to {Url}", hubUrl);
            ConnectionError?.Invoke($"Failed to connect: {ex.Message}");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            await _connection.StopAsync(cancellationToken);
        }
    }

    public async Task JoinSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_connection?.State == HubConnectionState.Connected)
        {
            await _connection.InvokeCoreAsync("JoinSessionGroup", typeof(object), new object[] { sessionId }, cancellationToken);
        }
    }

    public async Task LeaveSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_connection?.State == HubConnectionState.Connected)
        {
            await _connection.InvokeCoreAsync("LeaveSessionGroup", typeof(object), new object[] { sessionId }, cancellationToken);
        }
    }

    public async Task RequestSessionStatusAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_connection?.State == HubConnectionState.Connected)
        {
            await _connection.InvokeCoreAsync("RequestSessionStatus", typeof(object), new object[] { sessionId }, cancellationToken);
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
