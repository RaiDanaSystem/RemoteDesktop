using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SupportAgent.Services.Interfaces;

namespace SupportAgent.Services.Implementation;

public class TransportManager : ITransportManager
{
    private readonly ILogger<TransportManager> _logger;
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    public bool IsConnected => _tcpClient?.Connected == true;
    public TransportConnectionType ConnectionType { get; private set; }
    public TransportDiagnostics Diagnostics { get; } = new();

    public event Action<byte[]>? DataReceived;
    public event Action<string>? ConnectionStateChanged;

    public TransportManager(ILogger<TransportManager> logger)
    {
        _logger = logger;
    }

    public async Task<TransportConnectionResult> ConnectAsync(string serverUrl, Guid sessionId, string transportToken, CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = new Uri(serverUrl);
            var relayHost = uri.Host;
            var relayPort = 9999;

            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(relayHost, relayPort, cancellationToken);

            _networkStream = _tcpClient.GetStream();
            using var writer = new BinaryWriter(_networkStream, Encoding.UTF8, leaveOpen: true);
            using var reader = new BinaryReader(_networkStream, Encoding.UTF8, leaveOpen: true);

            // Send auth token
            var tokenBytes = Encoding.UTF8.GetBytes(transportToken);
            writer.Write(tokenBytes.Length);
            writer.Write(tokenBytes);
            await _networkStream.FlushAsync(cancellationToken);

            // Read auth response
            var success = reader.ReadBoolean();
            if (!success)
            {
                var errorMsg = reader.ReadString();
                return new TransportConnectionResult { IsSuccess = false, ErrorMessage = errorMsg };
            }

            ConnectionType = TransportConnectionType.Relay;
            Diagnostics.ConnectionType = TransportConnectionType.Relay;
            Diagnostics.ConnectionState = "Connected";
            Diagnostics.ConnectedAtUtc = DateTime.UtcNow;

            _receiveCts = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

            ConnectionStateChanged?.Invoke("Connected");
            _logger.LogInformation("Transport connected via relay to session {SessionId}", sessionId);

            return new TransportConnectionResult
            {
                IsSuccess = true,
                ConnectionType = TransportConnectionType.Relay
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transport connection failed");
            Diagnostics.FailureReason = ex.Message;
            Diagnostics.ConnectionState = "Failed";
            return new TransportConnectionResult { IsSuccess = false, ErrorMessage = $"Connection failed: {ex.Message}" };
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _receiveCts?.Cancel();
            if (_receiveTask is not null)
            {
                await Task.WhenAny(_receiveTask, Task.Delay(2000, cancellationToken));
            }

            _networkStream?.Close();
            _tcpClient?.Close();

            Diagnostics.ConnectionState = "Disconnected";
            ConnectionStateChanged?.Invoke("Disconnected");
        }
        catch { }
    }

    public async Task<int> SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (_networkStream is null || !IsConnected)
            throw new InvalidOperationException("Not connected.");

        using var writer = new BinaryWriter(_networkStream, Encoding.UTF8, leaveOpen: true);
        writer.Write(data.Length);
        writer.Write(data);
        await _networkStream.FlushAsync(cancellationToken);

        Diagnostics.BytesSent += data.Length;
        return data.Length;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        try
        {
            while (!cancellationToken.IsCancellationRequested && _networkStream is not null)
            {
                using var reader = new BinaryReader(_networkStream, Encoding.UTF8, leaveOpen: true);
                var length = reader.ReadInt32();
                if (length <= 0 || length > buffer.Length) break;

                var data = reader.ReadBytes(length);
                Diagnostics.BytesReceived += length;
                DataReceived?.Invoke(data);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Receive loop ended");
            Diagnostics.FailureReason = ex.Message;
            Diagnostics.ReconnectCount++;
            ConnectionStateChanged?.Invoke("Disconnected");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _receiveCts?.Dispose();
        _networkStream?.Dispose();
        _tcpClient?.Dispose();
    }
}
