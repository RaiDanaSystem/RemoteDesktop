using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using CustomerAgent;
using CustomerAgent.Services.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteSupport.Shared;
using RemoteSupport.Shared.Transport.Direct;
using RemoteSupport.Shared.Transport.WebRtc;
using SupportAgent.Services.Interfaces;

namespace SupportAgent.Services.Direct;

/// <summary>
/// Server-less LAN mode. While the app runs it
///  - advertises this PC on the local network and accepts incoming viewers (asking the user first), and
///  - lets the support UI list other PCs running the app and connect to them directly over TCP.
/// </summary>
public sealed class LanService : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILocalizationService _loc;
    private readonly AgentOptions _options;
    private readonly ILogger<LanService> _logger;
    private readonly string _settingsPath;
    private readonly object _hostingLock = new();

    private LanSettings _settings;
    private DirectHost? _host;
    private RemoteDesktopSession? _hostSession;
    private IServiceProvider? _hostServices;
    private bool _ending;

    public LanDiscovery? Discovery { get; private set; }
    public string SelfId => _settings.Id;
    public string SelfName => Environment.MachineName;
    public string? StartError { get; private set; }
    public bool IsHosting => _hostSession is not null;

    public event Action? HostingChanged;

    /// <summary>A viewer wants to see this PC. The UI must call Accept/Reject on the request.</summary>
    public event Action<DirectConnectionRequest>? IncomingRequest;

    public string HostingViewerName { get; private set; } = string.Empty;
    public bool HostingViewOnly { get; private set; }

    /// <summary>While someone views this PC with sound, play the sound only on the viewer (mute local speakers).</summary>
    public bool MuteSpeakersWhileShared
    {
        get => _settings.MuteWhileShared;
        set
        {
            _settings.MuteWhileShared = value;
            Save();
            var session = _hostSession;
            if (session is not null)
            {
                session.MuteSpeakersWhileStreamingAudio = value;
                session.ApplyAudioMutePolicy();
            }
        }
    }

    public bool SharingEnabled
    {
        get => _settings.Sharing;
        set
        {
            _settings.Sharing = value;
            if (Discovery is not null) Discovery.Announce = value;
            Save();
        }
    }

    public LanService(ILoggerFactory loggerFactory, ILocalizationService loc, AgentOptions options)
    {
        _loggerFactory = loggerFactory;
        _loc = loc;
        _options = options;
        _logger = loggerFactory.CreateLogger<LanService>();

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteSupport");
        _settingsPath = Path.Combine(dir, "lan.json");
        _settings = Load();
    }

    public void Start()
    {
        try
        {
            TryOpenFirewall();

            _host = new DirectHost(SelfName);
            var port = _host.Start();
            _host.ConnectionRequested += OnConnectionRequested;
            _host.ConnectionAccepted += OnConnectionAccepted;

            Discovery = new LanDiscovery(SelfId, SelfName) { TcpPort = port, Announce = _settings.Sharing };
            Discovery.Start();
            Discovery.Probe();
            _logger.LogInformation("LAN mode listening on TCP {Port}, discovery UDP {Udp}", port, DirectProtocol.DiscoveryPort);
        }
        catch (Exception ex)
        {
            StartError = ex.Message;
            _logger.LogWarning(ex, "LAN mode could not start");
        }
    }

    // ------------------------------------------------------------------ viewer side

    public Task<DirectConnectResult> ConnectAsync(IPAddress address, int port, CancellationToken ct = default)
        => DirectClient.ConnectAsync(address, port, SelfId, SelfName, "Windows", cancellationToken: ct);

    /// <summary>Parses "192.168.1.5", "192.168.1.5:45870" or "host-name[:port]".</summary>
    public static async Task<(IPAddress Address, int Port)?> ParseEndpointAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        var port = DirectProtocol.DefaultTcpPort;
        var host = text;
        var idx = text.LastIndexOf(':');
        if (idx > 0 && text.IndexOf(':') == idx && int.TryParse(text[(idx + 1)..], out var p) && p is > 0 and < 65536)
        {
            port = p;
            host = text[..idx];
        }

        if (IPAddress.TryParse(host, out var ip)) return (ip, port);
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host);
            var v4 = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return v4 is null ? null : (v4, port);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ host side

    private void OnConnectionRequested(DirectConnectionRequest request)
    {
        if (!_settings.Sharing)
        {
            request.Reject("Sharing is turned off on this computer.");
            return;
        }

        var handler = IncomingRequest;
        if (handler is null)
        {
            request.Reject("Not available.");
            return;
        }

        handler(request);
    }

    private void OnConnectionAccepted(object? sender, DirectAcceptedEventArgs e)
    {
        _ = Task.Run(() => StartHostingAsync(e));
    }

    private async Task StartHostingAsync(DirectAcceptedEventArgs e)
    {
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            lock (_hostingLock) _ending = false;

            var services = new ServiceCollection();
            services.AddSingleton(_loggerFactory);
            CustomerComposition.ConfigureServices(services, _options);
            var provider = services.BuildServiceProvider();
            var session = provider.GetRequiredService<RemoteDesktopSession>();
            var manager = provider.GetRequiredService<WebRtcSessionManager>();

            session.MuteSpeakersWhileStreamingAudio = _settings.MuteWhileShared;
            HostingViewerName = e.Request.ViewerName;
            HostingViewOnly = e.ViewOnly;
            _hostServices = provider;
            _hostSession = session;
            session.SessionEnded += (_, _) => _ = EndHostingAsync(sendDisconnect: false);

            await dispatcher.InvokeAsync(async () =>
            {
                await session.PrepareSessionAsync(Guid.NewGuid());
                session.SetInputConsent(!e.ViewOnly);
                session.SetClipboardConsent(true);
            }).Task.Unwrap();

            HostingChanged?.Invoke();
            await manager.AttachDirectChannelAsync(e.Channel, isInitiator: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start hosting session");
            try { await e.Channel.DisposeAsync(); } catch { }
            await EndHostingAsync(sendDisconnect: false);
        }
    }

    public Task StopHostingAsync() => EndHostingAsync(sendDisconnect: true);

    private async Task EndHostingAsync(bool sendDisconnect)
    {
        RemoteDesktopSession? session;
        IServiceProvider? provider;
        lock (_hostingLock)
        {
            if (_ending) return;
            _ending = true;
            session = _hostSession;
            provider = _hostServices;
            _hostSession = null;
            _hostServices = null;
        }

        try
        {
            if (session is not null && sendDisconnect)
                await session.SendDisconnectAsync();
        }
        catch { }

        try { if (session is not null) await session.DisposeAsync(); } catch { }
        try
        {
            if (provider is IAsyncDisposable ad) await ad.DisposeAsync();
            else if (provider is IDisposable d) d.Dispose();
        }
        catch { }

        HostingChanged?.Invoke();
    }

    // ------------------------------------------------------------------ settings / firewall

    private LanSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var s = JsonSerializer.Deserialize<LanSettings>(File.ReadAllText(_settingsPath));
                if (s is not null && !string.IsNullOrEmpty(s.Id)) return s;
            }
        }
        catch { }
        var created = new LanSettings { Id = Guid.NewGuid().ToString("N"), Sharing = true };
        _settings = created;
        Save();
        return created;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings));
        }
        catch { }
    }

    /// <summary>Best effort: needs admin; otherwise Windows shows its normal "allow access" prompt.</summary>
    private void TryOpenFirewall()
    {
        if (_settings.FirewallTried) return;
        _settings.FirewallTried = true;
        Save();
        _ = Task.Run(() =>
        {
            try
            {
                var exe = Environment.ProcessPath;
                foreach (var args in new[]
                {
                    $"advfirewall firewall add rule name=\"Rexon LAN (TCP)\" dir=in action=allow protocol=TCP localport={DirectProtocol.DefaultTcpPort}-{DirectProtocol.DefaultTcpPort + 19}",
                    $"advfirewall firewall add rule name=\"Rexon LAN (UDP)\" dir=in action=allow protocol=UDP localport={DirectProtocol.DiscoveryPort}"
                })
                {
                    using var p = Process.Start(new ProcessStartInfo("netsh", args)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    p?.WaitForExit(6000);
                }
            }
            catch { }
        });
    }

    public void Dispose()
    {
        try { Discovery?.Dispose(); } catch { }
        try { _host?.Dispose(); } catch { }
        try { EndHostingAsync(sendDisconnect: true).GetAwaiter().GetResult(); } catch { }
    }

    private sealed class LanSettings
    {
        public string Id { get; set; } = string.Empty;
        public bool Sharing { get; set; } = true;
        public bool FirewallTried { get; set; }
        public bool MuteWhileShared { get; set; }
    }
}
