using System.Windows;
using System.Windows.Controls;
using CustomerAgent.Services.Interfaces;
using CustomerAgent.Services.Session;
using CustomerAgent.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using RemoteSupport.Shared;

namespace CustomerAgent.Views;

public partial class CustomerWorkspace : UserControl
{
    private readonly IServiceProvider _services;
    private readonly AgentOptions _options;
    private CustomerViewModel? _viewModel;
    private RemoteDesktopSession? _session;
    private ISignalRClient? _signalR;
    private bool _started;

    public CustomerWorkspace(IServiceProvider services)
    {
        _services = services;
        _options = services.GetRequiredService<AgentOptions>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public bool HasLiveSession => _viewModel?.HasLiveSession == true;

    private int _stopped;

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
            return;

        if (_viewModel?.HasActiveSession == true)
        {
            try
            {
                await _viewModel.DisconnectCommand.ExecuteAsync(null);
            }
            catch { }
        }

        if (_signalR is not null)
        {
            _signalR.ConnectionRequestReceived -= OnSignalRConnectionRequest;
            await _signalR.DisposeAsync();
            _signalR = null;
        }

        _viewModel?.StopPolling();
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
            return;
        _started = true;

        _viewModel = _services.GetRequiredService<CustomerViewModel>();
        _session = _services.GetRequiredService<RemoteDesktopSession>();
        _signalR = _services.GetRequiredService<ISignalRClient>();
        DataContext = _viewModel;

        _signalR.ConnectionRequestReceived += OnSignalRConnectionRequest;

        _viewModel.OnSessionAccepted += async (_, args) =>
        {
            await _session.PrepareSessionAsync(args.SessionId);
            _session.SetInputConsent(_viewModel.AllowRemoteControl);
            _session.SetClipboardConsent(_viewModel.AllowClipboard);
            _session.SetFileTransferConsent(_viewModel.AllowFileTransfer);
        };
        _viewModel.OnSessionDisconnecting += async (_, _) =>
        {
            await _session.SendDisconnectAsync();
        };
        _viewModel.RemoteControlChanged += granted => _session.SetInputConsent(granted);
        _viewModel.ClipboardChanged += granted => _session.SetClipboardConsent(granted);
        _viewModel.IncomingFileDecision += accept => _ = _session.RespondToFileOfferAsync(accept);
        _session.IncomingFileOffered += (_, args) =>
        {
            Dispatcher.Invoke(() =>
            {
                _viewModel.IncomingFileName = $"{args.FileName} ({args.FileSize} bytes)";
                _viewModel.ShowIncomingFile = true;
            });
        };
        _session.SessionEnded += (_, _) =>
        {
            Dispatcher.Invoke(() => _viewModel.NotifyRemoteSessionEnded());
        };

        await _viewModel.GenerateSupportCodeCommand.ExecuteAsync(null);

        try
        {
            await _session.ConnectSignalingAsync(_options.ServerUrl, _viewModel.DeviceIdentifier);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CUSTOMER] Signaling connection failed: {ex.Message}");
        }

        try
        {
            await _signalR.StartAsync(_options.ServerUrl, _viewModel.DeviceIdentifier);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CUSTOMER] Session hub failed: {ex.Message}");
        }
    }

    private void OnSignalRConnectionRequest(Guid sessionId, string agentName, string? agentDisplayName, string agentRole, DateTime requestedAt)
    {
        Dispatcher.Invoke(() =>
        {
            _viewModel?.ShowIncomingRequest(sessionId, agentDisplayName ?? agentName, agentRole);
        });
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnUnloaded;
        await StopAsync();
    }
}
