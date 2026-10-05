namespace CustomerAgent.Services.Interfaces;

public interface ISignalRClient : IAsyncDisposable
{
    Task StartAsync(string serverUrl, string deviceIdentifier, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    bool IsConnected { get; }

    event Action<Guid, string, string?, string, DateTime>? ConnectionRequestReceived;
    event Action<Guid, string, string?, DateTime?, string?>? SessionStateChanged;
    event Action<Guid, string, string?, DateTime?>? SessionTerminated;
    event Action<string>? ConnectionError;
    event Action? Connected;
    event Action? Disconnected;
}
