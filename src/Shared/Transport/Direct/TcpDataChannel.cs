using System.Net.Sockets;

namespace RemoteSupport.Shared.Transport.Direct;

/// <summary>
/// <see cref="IDataChannel"/> over a connected TCP socket. Carries serialized TransportEnvelopes.
/// Writes are serialized; Nagle is disabled for low latency.
/// </summary>
public sealed class TcpDataChannel : IDataChannel
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private DataChannelState _state = DataChannelState.New;
    private int _closedRaised;

    public string PeerId { get; }
    public string RemoteName { get; }
    public DataChannelState State => _state;

    public event EventHandler<ReadOnlyMemory<byte>>? MessageReceived;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<Exception>? Error;

    public TcpDataChannel(TcpClient client, string peerId, string remoteName)
    {
        _client = client;
        _client.NoDelay = true;
        _client.ReceiveBufferSize = 4 * 1024 * 1024;
        _client.SendBufferSize = 4 * 1024 * 1024;
        _stream = client.GetStream();
        PeerId = peerId;
        RemoteName = remoteName;
    }

    /// <summary>Starts the receive loop and raises <see cref="Opened"/>. Subscribe to events first.</summary>
    public void Start()
    {
        _state = DataChannelState.Connected;
        Opened?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(ReceiveLoopAsync);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_state != DataChannelState.Connected) return;

        var frame = DirectProtocol.Frame(DirectProtocol.TagData, data.Span);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            await _stream.WriteAsync(frame, linked.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error?.Invoke(this, ex);
            RaiseClosed(DataChannelState.Failed);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await DirectProtocol.ReadFrameAsync(_stream, _cts.Token);
                if (frame is null) break;
                if (frame.Value.Tag == DirectProtocol.TagData)
                    MessageReceived?.Invoke(this, frame.Value.Body);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error?.Invoke(this, ex);
        }
        finally
        {
            RaiseClosed(DataChannelState.Closed);
        }
    }

    private void RaiseClosed(DataChannelState finalState)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 1) return;
        _state = finalState;
        try { _cts.Cancel(); } catch { }
        try { _client.Close(); } catch { }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        RaiseClosed(DataChannelState.Closed);
        return ValueTask.CompletedTask;
    }
}
