using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace RemoteSupport.Shared.Transport.Direct;

/// <summary>
/// UDP-broadcast discovery of computers running the app on the local network.
/// - Announces this PC (if <see cref="Announce"/> is set) every <see cref="BeaconInterval"/>.
/// - Listens for other beacons and maintains <see cref="Peers"/>.
/// - <see cref="Probe"/> asks every host to answer immediately.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    private readonly ConcurrentDictionary<string, LanPeer> _peers = new();
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _listener;
    private UdpClient? _sender;
    private Task? _listenTask;
    private Task? _beaconTask;

    public string SelfId { get; }
    public string SelfName { get; set; }
    public string Platform { get; set; } = "Windows";
    public int TcpPort { get; set; } = DirectProtocol.DefaultTcpPort;
    public int DiscoveryPort { get; }
    public TimeSpan BeaconInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan PeerTimeout { get; set; } = TimeSpan.FromSeconds(7);

    /// <summary>When true this PC advertises itself as shareable.</summary>
    public bool Announce { get; set; } = true;

    public event Action? PeersChanged;

    public LanDiscovery(string selfId, string selfName, int discoveryPort = DirectProtocol.DiscoveryPort)
    {
        SelfId = selfId;
        SelfName = selfName;
        DiscoveryPort = discoveryPort;
    }

    public IReadOnlyList<LanPeer> Peers
    {
        get
        {
            var now = DateTime.UtcNow;
            return _peers.Values
                .Where(p => now - p.LastSeenUtc < PeerTimeout)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void Start()
    {
        _listener = new UdpClient(AddressFamily.InterNetwork);
        _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        _sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };

        _listenTask = Task.Run(ListenLoopAsync);
        _beaconTask = Task.Run(BeaconLoopAsync);
    }

    /// <summary>Asks all hosts on the network to announce themselves now.</summary>
    public void Probe()
    {
        var msg = new BeaconMessage { Id = SelfId, Name = SelfName, Platform = Platform, Port = TcpPort, Probe = true, Sharing = Announce };
        SendBroadcast(msg);
    }

    private async Task BeaconLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (Announce)
                    SendBroadcast(new BeaconMessage { Id = SelfId, Name = SelfName, Platform = Platform, Port = TcpPort, Sharing = true });

                if (PruneExpired())
                    PeersChanged?.Invoke();

                await Task.Delay(BeaconInterval, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private bool PruneExpired()
    {
        var removed = false;
        var now = DateTime.UtcNow;
        foreach (var kv in _peers)
        {
            if (now - kv.Value.LastSeenUtc >= PeerTimeout && _peers.TryRemove(kv.Key, out _))
                removed = true;
        }
        return removed;
    }

    private void SendBroadcast(BeaconMessage msg)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(msg, DirectProtocol.Json);
        foreach (var target in BroadcastTargets())
        {
            try { _sender?.Send(data, data.Length, new IPEndPoint(target, DiscoveryPort)); }
            catch { }
        }
    }

    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        var list = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var bc = new byte[4];
                    for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    list.Add(new IPAddress(bc));
                }
            }
        }
        catch { }
        return list;
    }

    private async Task ListenLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _listener!.ReceiveAsync(_cts.Token);
                BeaconMessage? msg;
                try { msg = JsonSerializer.Deserialize<BeaconMessage>(result.Buffer, DirectProtocol.Json); }
                catch { continue; }
                if (msg is null || msg.App != DirectProtocol.ProtocolId || string.IsNullOrEmpty(msg.Id)) continue;
                if (msg.Id == SelfId) continue;

                if (msg.Probe)
                {
                    if (Announce)
                    {
                        var reply = JsonSerializer.SerializeToUtf8Bytes(
                            new BeaconMessage { Id = SelfId, Name = SelfName, Platform = Platform, Port = TcpPort, Sharing = true },
                            DirectProtocol.Json);
                        try { _sender?.Send(reply, reply.Length, new IPEndPoint(result.RemoteEndPoint.Address, DiscoveryPort)); } catch { }
                    }
                    continue;
                }

                if (!msg.Sharing)
                {
                    if (_peers.TryRemove(msg.Id, out _)) PeersChanged?.Invoke();
                    continue;
                }

                var isNew = !_peers.ContainsKey(msg.Id);
                var peer = new LanPeer
                {
                    Id = msg.Id,
                    Name = msg.Name,
                    Platform = msg.Platform,
                    Address = result.RemoteEndPoint.Address,
                    Port = msg.Port,
                    LastSeenUtc = DateTime.UtcNow
                };
                var changed = isNew || _peers[msg.Id].Address.ToString() != peer.Address.ToString()
                              || _peers[msg.Id].Name != peer.Name || _peers[msg.Id].Port != peer.Port;
                _peers[msg.Id] = peer;
                if (changed) PeersChanged?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch { }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Close(); } catch { }
        try { _sender?.Close(); } catch { }
    }
}
