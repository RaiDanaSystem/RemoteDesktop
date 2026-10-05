using System.Net;
using System.Net.Sockets;

namespace RemoteSupport.Shared.Transport.Direct;

public sealed class DirectConnectResult
{
    public bool IsSuccess { get; init; }
    public string? Error { get; init; }
    public TcpDataChannel? Channel { get; init; }
    public string HostName { get; init; } = string.Empty;
    public bool ViewOnly { get; init; }
}

/// <summary>Viewer side of direct mode: connect, say hello, wait for the host's decision.</summary>
public static class DirectClient
{
    public static async Task<DirectConnectResult> ConnectAsync(
        IPAddress address, int port, string viewerId, string viewerName, string platform = "Windows",
        TimeSpan? decisionTimeout = null, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(6));
                await client.ConnectAsync(address, port, connectCts.Token);
            }

            var stream = client.GetStream();
            await stream.WriteAsync(DirectProtocol.FrameJson(DirectProtocol.TagHello, new HelloMessage
            {
                ViewerId = viewerId,
                ViewerName = viewerName,
                Platform = platform
            }), cancellationToken);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(decisionTimeout ?? TimeSpan.FromSeconds(75));
            var frame = await DirectProtocol.ReadFrameAsync(stream, waitCts.Token);
            if (frame is null)
            {
                client.Close();
                return new DirectConnectResult { Error = "The computer closed the connection." };
            }

            if (frame.Value.Tag == DirectProtocol.TagReject)
            {
                var rej = DirectProtocol.Parse<RejectMessage>(frame.Value.Body);
                client.Close();
                return new DirectConnectResult { Error = rej?.Reason ?? "Rejected" };
            }

            if (frame.Value.Tag != DirectProtocol.TagAccept)
            {
                client.Close();
                return new DirectConnectResult { Error = "Unexpected reply from the computer." };
            }

            var acc = DirectProtocol.Parse<AcceptMessage>(frame.Value.Body) ?? new AcceptMessage();
            var channel = new TcpDataChannel(client, Guid.NewGuid().ToString("N"), acc.HostName);
            return new DirectConnectResult
            {
                IsSuccess = true,
                Channel = channel,
                HostName = acc.HostName,
                ViewOnly = acc.ViewOnly
            };
        }
        catch (OperationCanceledException)
        {
            client.Close();
            return new DirectConnectResult { Error = "Timed out waiting for the computer." };
        }
        catch (Exception ex)
        {
            client.Close();
            return new DirectConnectResult { Error = ex.Message };
        }
    }
}
