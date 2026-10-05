using System.Net;
using System.Net.Sockets;

namespace RemoteSupport.Shared.Transport.Direct;

/// <summary>An incoming viewer asking to see this PC. Call <see cref="Accept"/> or <see cref="Reject"/>.</summary>
public sealed class DirectConnectionRequest
{
    private readonly TaskCompletionSource<(bool Accepted, bool ViewOnly, string? Reason)> _decision = new();

    public string ViewerId { get; init; } = string.Empty;
    public string ViewerName { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public IPAddress RemoteAddress { get; init; } = IPAddress.None;

    /// <summary>Raised once when the request was accepted, rejected or timed out.</summary>
    public event Action? Decided;

    public bool IsDecided => _decision.Task.IsCompleted;

    public void Accept(bool viewOnly = false)
    {
        if (_decision.TrySetResult((true, viewOnly, null))) Decided?.Invoke();
    }

    public void Reject(string reason = "Rejected by the user")
    {
        if (_decision.TrySetResult((false, false, reason))) Decided?.Invoke();
    }

    internal Task<(bool Accepted, bool ViewOnly, string? Reason)> Decision => _decision.Task;
}

public sealed class DirectAcceptedEventArgs : EventArgs
{
    public required TcpDataChannel Channel { get; init; }
    public required DirectConnectionRequest Request { get; init; }
    public bool ViewOnly { get; init; }
}

/// <summary>
/// Listens for viewers on a TCP port. After the Hello handshake the host's UI decides
/// (via <see cref="ConnectionRequested"/>); an accepted connection is surfaced as a ready-to-use channel.
/// </summary>
public sealed class DirectHost : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private int _active;

    public string HostName { get; set; }
    public int Port { get; private set; }

    /// <summary>Raised for each incoming viewer; the handler must eventually call Accept/Reject.</summary>
    public event Action<DirectConnectionRequest>? ConnectionRequested;

    /// <summary>Raised when a viewer was accepted. The channel is not started yet (subscribe, then call Start).</summary>
    public event EventHandler<DirectAcceptedEventArgs>? ConnectionAccepted;

    /// <summary>When true, new viewers are rejected as "busy" (a session is already running).</summary>
    public bool Busy { get; set; }

    public TimeSpan DecisionTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public DirectHost(string hostName) => HostName = hostName;

    /// <summary>Binds the first free port starting at <paramref name="preferredPort"/>.</summary>
    public int Start(int preferredPort = DirectProtocol.DefaultTcpPort)
    {
        for (var port = preferredPort; port < preferredPort + 20; port++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Start();
                _listener = l;
                Port = port;
                _ = Task.Run(AcceptLoopAsync);
                return port;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("No free TCP port for direct mode.");
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleClientAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch { }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        var keep = false;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            helloCts.CancelAfter(TimeSpan.FromSeconds(10));
            var frame = await DirectProtocol.ReadFrameAsync(stream, helloCts.Token);
            if (frame is null || frame.Value.Tag != DirectProtocol.TagHello) return;
            var hello = DirectProtocol.Parse<HelloMessage>(frame.Value.Body);
            if (hello is null || hello.Protocol != DirectProtocol.ProtocolId) return;

            if (Busy || Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            {
                await stream.WriteAsync(DirectProtocol.FrameJson(DirectProtocol.TagReject, new RejectMessage { Reason = "busy" }), _cts.Token);
                return;
            }

            try
            {
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
                var request = new DirectConnectionRequest
                {
                    ViewerId = hello.ViewerId,
                    ViewerName = hello.ViewerName,
                    Platform = hello.Platform,
                    RemoteAddress = remote
                };

                if (ConnectionRequested is null)
                {
                    await stream.WriteAsync(DirectProtocol.FrameJson(DirectProtocol.TagReject, new RejectMessage { Reason = "not available" }), _cts.Token);
                    return;
                }

                ConnectionRequested.Invoke(request);

                var decisionTask = request.Decision;
                var winner = await Task.WhenAny(decisionTask, Task.Delay(DecisionTimeout, _cts.Token));
                if (winner != decisionTask)
                {
                    request.Reject("no answer");
                    await stream.WriteAsync(DirectProtocol.FrameJson(DirectProtocol.TagReject, new RejectMessage { Reason = "no answer" }), _cts.Token);
                    return;
                }

                var decision = await decisionTask;
                if (!decision.Accepted)
                {
                    await stream.WriteAsync(DirectProtocol.FrameJson(DirectProtocol.TagReject,
                        new RejectMessage { Reason = decision.Reason ?? "Rejected" }), _cts.Token);
                    return;
                }

                await stream.WriteAsync(DirectProtocol.FrameJson(DirectProtocol.TagAccept,
                    new AcceptMessage { HostName = HostName, ViewOnly = decision.ViewOnly }), _cts.Token);

                var channel = new TcpDataChannel(client, hello.ViewerId, hello.ViewerName);
                channel.Closed += (_, _) => Interlocked.Exchange(ref _active, 0);
                keep = true;
                ConnectionAccepted?.Invoke(this, new DirectAcceptedEventArgs
                {
                    Channel = channel,
                    Request = request,
                    ViewOnly = decision.ViewOnly
                });
            }
            finally
            {
                if (!keep) Interlocked.Exchange(ref _active, 0);
            }
        }
        catch
        {
        }
        finally
        {
            if (!keep)
            {
                try { client.Close(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
    }
}
