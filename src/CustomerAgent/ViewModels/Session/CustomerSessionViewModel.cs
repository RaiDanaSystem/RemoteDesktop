using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CustomerAgent.Services.Interfaces;

namespace CustomerAgent.ViewModels.Session;

public partial class CustomerSessionViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;

    [ObservableProperty] private bool _isActiveSession;
    [ObservableProperty] private string _agentName = string.Empty;
    [ObservableProperty] private string _agentRole = string.Empty;
    [ObservableProperty] private string _sessionDuration = "00:00:00";
    [ObservableProperty] private string _connectionStatus = "No active session";
    [ObservableProperty] private string _connectionStatusColor = "#6B7280";

    // Permissions
    [ObservableProperty] private bool _screenSharingActive;
    [ObservableProperty] private bool _remoteControlEnabled;
    [ObservableProperty] private bool _clipboardEnabled;
    [ObservableProperty] private bool _fileTransferEnabled;
    [ObservableProperty] private bool _audioEnabled;

    private DateTime _sessionStartUtc;
    private System.Windows.Threading.DispatcherTimer? _sessionTimer;

    public CustomerSessionViewModel(ILocalizationService localization)
    {
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public event EventHandler? OnEndSessionRequested;

    [RelayCommand]
    private void EndSession()
    {
        StopTimer();
        IsActiveSession = false;
        ScreenSharingActive = false;
        RemoteControlEnabled = false;
        ClipboardEnabled = false;
        FileTransferEnabled = false;
        AudioEnabled = false;
        ConnectionStatus = "No active session";
        ConnectionStatusColor = "#6B7280";
        SessionDuration = "00:00:00";
        OnEndSessionRequested?.Invoke(this, EventArgs.Empty);
    }

    public void StartSession(string agentName, string agentRole)
    {
        IsActiveSession = true;
        AgentName = agentName;
        AgentRole = agentRole;
        ScreenSharingActive = true;
        _sessionStartUtc = DateTime.UtcNow;
        ConnectionStatus = "Active session with " + agentName;
        ConnectionStatusColor = "#10B981";

        StartTimer();
    }

    private void StartTimer()
    {
        StopTimer();
        _sessionTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _sessionTimer.Tick += OnTimerTick;
        _sessionTimer.Start();
    }

    private void StopTimer()
    {
        if (_sessionTimer is not null)
        {
            _sessionTimer.Stop();
            _sessionTimer.Tick -= OnTimerTick;
            _sessionTimer = null;
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (IsActiveSession)
        {
            var elapsed = DateTime.UtcNow - _sessionStartUtc;
            SessionDuration = elapsed.ToString(@"hh\:mm\:ss");
        }
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(ScreenSharingLabel));
        OnPropertyChanged(nameof(RemoteControlLabel));
        OnPropertyChanged(nameof(ClipboardLabel));
        OnPropertyChanged(nameof(FileTransferLabel));
        OnPropertyChanged(nameof(AudioLabel));
        OnPropertyChanged(nameof(EndSessionLabel));
    }

    public string ScreenSharingLabel => _localization.GetString("Customer_ScreenSharing");
    public string RemoteControlLabel => string.Format(
        _localization.GetString("Customer_RemoteControl"),
        RemoteControlEnabled ? "Enabled" : "Disabled");
    public string ClipboardLabel => string.Format(
        _localization.GetString("Customer_ClipboardAccess"),
        ClipboardEnabled ? "Allowed" : "Blocked");
    public string FileTransferLabel => string.Format(
        _localization.GetString("Customer_FileTransfer"),
        FileTransferEnabled ? "Allowed" : "Blocked");
    public string AudioLabel => string.Format(
        _localization.GetString("Customer_AudioAccess"),
        AudioEnabled ? "Active" : "Muted");
    public string EndSessionLabel => _localization.GetString("Customer_EndSession");
}
