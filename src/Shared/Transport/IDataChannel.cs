namespace RemoteSupport.Shared.Transport;

/// <summary>
/// Represents a bidirectional data channel for sending and receiving messages between peers.
/// Implementations must be thread-safe for concurrent send/receive operations.
/// </summary>
public interface IDataChannel : IAsyncDisposable
{
    DataChannelState State { get; }

    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    event EventHandler<ReadOnlyMemory<byte>>? MessageReceived;

    event EventHandler? Opened;

    event EventHandler? Closed;

    event EventHandler<Exception>? Error;
}

public enum DataChannelState
{
    New,
    Connecting,
    Connected,
    Closing,
    Closed,
    Failed
}
