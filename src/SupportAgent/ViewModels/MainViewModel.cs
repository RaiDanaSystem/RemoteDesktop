using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SupportAgent.Models;
using SupportAgent.Services.Interfaces;
using SupportAgent.Services.Session;
using SupportAgent.ViewModels.Session;
using Microsoft.Extensions.DependencyInjection;
using RemoteSupport.Shared;

namespace SupportAgent.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly IApiClient _apiClient;
    private readonly ITokenStorage _tokenStorage;
    private readonly INavigationService _navigationService;
    private readonly ILocalizationService _localizationService;
    private readonly ISignalRClient _signalRClient;
    private readonly SessionViewModel _sessionViewModel;
    private readonly AgentOptions _agentOptions;
    private readonly IServiceProvider _services;
    private RemoteDesktopSession? _remoteDesktopSession;
    private Guid? _activeWebRtcSessionId;

    [ObservableProperty]
    private UserSession? _currentSession;

    [ObservableProperty]
    private ViewModelBase _currentViewModel;

    [ObservableProperty]
    private string _supportCodeInput = string.Empty;

    [ObservableProperty]
    private string _connectionStatus = "Disconnected";

    [ObservableProperty]
    private string _connectionStatusColor = "#EF4444";

    public SessionViewModel SessionViewModel => _sessionViewModel;

    public string WelcomeMessage => string.Format(
        _localizationService.GetString("Dashboard_Welcome"),
        CurrentSession?.DisplayName ?? CurrentSession?.Username ?? "Agent");

    public string UserRole => CurrentSession?.Role ?? string.Empty;
    public string LogoutButton => _localizationService.GetString("Dashboard_Logout");
    public string ConnectButton => _localizationService.GetString("Dashboard_Connect");
    public string SupportCodeLabel => _localizationService.GetString("Dashboard_SupportCode");
    public string ActiveSessionsTitle => _localizationService.GetString("Dashboard_ActiveSessions");
    public string SettingsButton => _localizationService.GetString("Dashboard_Settings");
    public string LanguageButton => _localizationService.IsRtl ? "English" : "فارسی";
    public string AppTitle => _localizationService.GetString("App_Title");
    public string SupportCodePlaceholder => _localizationService.GetString("Dashboard_CodePlaceholder");
    public string ConnectHint => _localizationService.GetString("Dashboard_ConnectHint");
    public FlowDirection LayoutDirection =>
        _localizationService.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public MainViewModel(
        IApiClient apiClient,
        ITokenStorage tokenStorage,
        INavigationService navigationService,
        ILocalizationService localizationService,
        ISignalRClient signalRClient,
        SessionViewModel sessionViewModel,
        AgentOptions agentOptions,
        IServiceProvider services)
    {
        _apiClient = apiClient;
        _tokenStorage = tokenStorage;
        _navigationService = navigationService;
        _localizationService = localizationService;
        _signalRClient = signalRClient;
        _sessionViewModel = sessionViewModel;
        _agentOptions = agentOptions;
        _services = services;
        _currentViewModel = sessionViewModel;
        _sessionViewModel.SessionTeardownRequested += TerminateActiveSessionAsync;

        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public void SetSession(UserSession session)
    {
        CurrentSession = session;
        _apiClient.SetAccessToken(session.AccessToken);
        OnPropertyChanged(nameof(WelcomeMessage));
        OnPropertyChanged(nameof(UserRole));

        _ = ConnectSignalRAsync(session.AccessToken);
    }

    private async Task ConnectSignalRAsync(string accessToken)
    {
        try
        {
            _signalRClient.SessionStateChanged += OnSessionStateChanged;
            _signalRClient.SessionTerminated += OnSessionTerminated;
            _signalRClient.ConnectionError += OnSignalRError;

            await _signalRClient.StartAsync(_agentOptions.ServerUrl, accessToken);
            System.Diagnostics.Debug.WriteLine("[SIGNALR] SupportAgent connected to session hub");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SIGNALR] Connection failed: {ex.Message}");
        }
    }

    private void OnSessionStateChanged(SessionStateChangedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[SIGNALR] Session {e.SessionId} state: {e.Status}");

        if (string.Equals(e.Status, "Active", StringComparison.OrdinalIgnoreCase)
            && e.SessionId != Guid.Empty)
        {
            // WebRTC/SIPSorcery must not run on the WPF UI thread (createDataChannel can hang).
            _ = Task.Run(async () =>
            {
                try
                {
                    await StartRemoteDesktopAsync(e.SessionId, e.CustomerDeviceName ?? "Unknown");
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        StatusMessage = $"WebRTC failed: {ex.Message}";
                        HasError = true;
                    });
                }
            });
        }
    }

    private void OnSessionTerminated(SessionTerminatedEventArgs e)
    {
        _activeWebRtcSessionId = null;
        if (_remoteDesktopSession is not null)
        {
            _sessionViewModel.DetachSession();
            _ = _remoteDesktopSession.DisposeAsync();
            _remoteDesktopSession = null;
        }

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _sessionViewModel.ResetIdleUi();
            ConnectionStatus = _localizationService.GetString("Dashboard_Disconnected");
            ConnectionStatusColor = "#EF4444";
        });
    }

    private void OnSignalRError(string error)
    {
        System.Diagnostics.Debug.WriteLine($"[SIGNALR] Error: {error}");
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (CurrentSession is not null)
        {
            await _apiClient.LogoutAsync(CurrentSession.RefreshToken);
        }

        _tokenStorage.ClearTokens();
        _apiClient.SetAccessToken(null);
        CurrentSession = null;

        RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(SupportCodeInput))
        {
            StatusMessage = _localizationService.GetString("Dashboard_EnterCode");
            HasError = true;
            return;
        }

        IsBusy = true;
        ConnectionStatus = _localizationService.GetString("Dashboard_Connecting");
        ConnectionStatusColor = "#F59E0B";
        ClearError();

        try
        {
            var trimmed = SupportCodeInput.Trim();
            var digits = new string(trimmed.Where(char.IsDigit).ToArray());
            var code = digits.Length > 0 ? digits : trimmed.ToUpperInvariant();
            var result = await _apiClient.ConnectAsync(code);

            if (result.IsSuccess && result.SessionId.HasValue)
            {
                ConnectionStatus = _localizationService.GetString("Dashboard_Connecting");
                ConnectionStatusColor = "#F59E0B";
                StatusMessage = string.Format(
                    _localizationService.GetString("Dashboard_ConnectedTo"),
                    result.CustomerDeviceName ?? SupportCodeInput);
                HasError = false;

                _sessionViewModel.StartSession(
                    result.SessionId.Value.ToString(),
                    result.CustomerDeviceName ?? "Unknown");

                try
                {
                    await _signalRClient.JoinSessionAsync(result.SessionId.Value);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Join session failed: {ex.Message}";
                }
            }
            else
            {
                ConnectionStatus = _localizationService.GetString("Dashboard_Disconnected");
                ConnectionStatusColor = "#EF4444";
                SetError(result.ErrorMessage ?? _localizationService.GetString("Dashboard_ConnectFailed"));
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus = _localizationService.GetString("Dashboard_Disconnected");
            ConnectionStatusColor = "#EF4444";
            SetError($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartRemoteDesktopAsync(Guid sessionId, string customerDevice)
    {
        try
        {
            if (CurrentSession is null) return;
            if (_activeWebRtcSessionId == sessionId && _remoteDesktopSession is not null)
                return;

            if (_remoteDesktopSession is not null)
            {
                _sessionViewModel.DetachSession();
                await _remoteDesktopSession.DisposeAsync();
            }
            _remoteDesktopSession = _services.GetRequiredService<RemoteDesktopSession>();
            _activeWebRtcSessionId = sessionId;

            System.Windows.UIElement? captureElement = null;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _sessionViewModel.AttachSession(_remoteDesktopSession);
                captureElement = System.Windows.Application.Current.Windows
                    .OfType<Views.ShellWindow>()
                    .FirstOrDefault()?.ScreenCaptureElement;
            });

            await _remoteDesktopSession.ConnectSignalingAsync(_agentOptions.ServerUrl, CurrentSession.AccessToken);
            await _remoteDesktopSession.StartSessionAsync(sessionId, customerDevice,
                source => { }, captureElement);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ConnectionStatus = _localizationService.GetString("Dashboard_Connected");
                ConnectionStatusColor = "#10B981";
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WEBRTC] Failed to start session: {ex.Message}");
            StatusMessage = $"WebRTC failed: {ex.Message}";
            HasError = true;
        }
    }

    [RelayCommand]
    private void ToggleLanguage()
    {
        var newLang = _localizationService.IsRtl ? "en" : "fa";
        _localizationService.SetLanguage(newLang);
    }

    public async Task TerminateActiveSessionAsync()
    {
        if (_activeWebRtcSessionId is Guid sessionId)
        {
            await _apiClient.DisconnectSessionAsync(sessionId);
        }

        if (_remoteDesktopSession is not null)
        {
            _sessionViewModel.DetachSession();
            await _remoteDesktopSession.DisposeAsync();
            _remoteDesktopSession = null;
        }

        _activeWebRtcSessionId = null;
        SessionViewModel.ResetIdleUi();
        ConnectionStatus = _localizationService.GetString("Dashboard_Disconnected");
        ConnectionStatusColor = "#EF4444";
    }

    public bool HasLiveSession =>
        _activeWebRtcSessionId.HasValue || SessionViewModel.IsConnected;

    public event Action? RequestClose;

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(WelcomeMessage));
        OnPropertyChanged(nameof(LogoutButton));
        OnPropertyChanged(nameof(ConnectButton));
        OnPropertyChanged(nameof(SupportCodeLabel));
        OnPropertyChanged(nameof(ActiveSessionsTitle));
        OnPropertyChanged(nameof(SettingsButton));
        OnPropertyChanged(nameof(LanguageButton));
        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(SupportCodePlaceholder));
        OnPropertyChanged(nameof(ConnectHint));
        OnPropertyChanged(nameof(LayoutDirection));
    }
}
