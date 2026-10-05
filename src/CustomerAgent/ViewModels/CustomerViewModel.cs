using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CustomerAgent.Models;
using CustomerAgent.Services.Interfaces;

namespace CustomerAgent.ViewModels;

public partial class CustomerViewModel : ViewModelBase
{
    private readonly ICustomerApiClient _apiClient;
    private readonly ILocalizationService _localizationService;
    private Timer? _pollTimer;
    private string _deviceIdentifier;

    [ObservableProperty]
    private string _supportCode = "---";

    [ObservableProperty]
    private string _codeExpiry = string.Empty;

    [ObservableProperty]
    private ConnectionState _connectionState = ConnectionState.Offline;

    [ObservableProperty]
    private bool _isCodeActive;

    [ObservableProperty]
    private bool _hasActiveSession;

    [ObservableProperty]
    private bool _showConnectionRequest;

    [ObservableProperty]
    private string _requestingAgentName = string.Empty;

    [ObservableProperty]
    private string _requestingAgentRole = string.Empty;

    [ObservableProperty]
    private Guid _pendingSessionId;

    [ObservableProperty]
    private string _sessionDuration = "00:00:00";

    [ObservableProperty]
    private string _connectionStatusText = "Offline";

    [ObservableProperty]
    private string _connectionStatusColor = "#6B7280";

    [ObservableProperty]
    private bool _showActiveSessionPanel;

    [ObservableProperty]
    private bool _allowRemoteControl = true;

    [ObservableProperty]
    private bool _allowClipboard = true;

    [ObservableProperty]
    private bool _allowFileTransfer = true;

    [ObservableProperty]
    private string _incomingFileName = string.Empty;

    [ObservableProperty]
    private bool _showIncomingFile;

    public event Action<bool>? RemoteControlChanged;
    public event Action<bool>? ClipboardChanged;
    public event Action<bool>? IncomingFileDecision;

    partial void OnAllowRemoteControlChanged(bool value) => RemoteControlChanged?.Invoke(value);
    partial void OnAllowClipboardChanged(bool value) => ClipboardChanged?.Invoke(value);
    partial void OnIncomingFileNameChanged(string value) => OnPropertyChanged(nameof(IncomingFileLabel));
    partial void OnRequestingAgentNameChanged(string value)
    {
        OnPropertyChanged(nameof(AgentWantsToConnectText));
        OnPropertyChanged(nameof(ConnectedToFormatted));
    }

    [RelayCommand]
    private void AcceptIncomingFile()
    {
        ShowIncomingFile = false;
        IncomingFileDecision?.Invoke(true);
    }

    [RelayCommand]
    private void RejectIncomingFile()
    {
        ShowIncomingFile = false;
        IncomingFileDecision?.Invoke(false);
    }

    public string AppTitle => _localizationService.GetString("App_Title");
    public string SupportIdLabel => _localizationService.GetString("Customer_SupportId");
    public string ShareCodeInstruction => _localizationService.GetString("Customer_ShareCode");
    public string CodeExpiresLabel => _localizationService.GetString("Customer_Expires");
    public string RefreshCodeButton => _localizationService.GetString("Customer_RefreshCode");
    public string ConnectionRequestTitle => _localizationService.GetString("Customer_ConnRequestTitle");
    public string AcceptButton => _localizationService.GetString("Customer_Accept");
    public string RejectButton => _localizationService.GetString("Customer_Reject");
    public string DisconnectButton => _localizationService.GetString("Customer_Disconnect");
    public string ActiveSessionLabel => _localizationService.GetString("Customer_ActiveSession");
    public string ConnectedToLabel => _localizationService.GetString("Customer_ConnectedTo");
    public string SupportActiveWarning => _localizationService.GetString("Customer_SupportActiveWarning");
    public string ConnectedToAgentText => _localizationService.GetString("Customer_ConnectedToAgent");
    public string LanguageButton => _localizationService.IsRtl ? "English" : "فارسی";
    public string DeviceIdentifier => _deviceIdentifier;
    public bool HasLiveSession => HasActiveSession || ShowConnectionRequest;

    public string BrandTagline => _localizationService.GetString("Customer_BrandTagline");
    public string AllowRemoteLabel => _localizationService.GetString("Customer_AllowRemote");
    public string AllowClipboardLabel => _localizationService.GetString("Customer_AllowClipboard");
    public string AllowFilesLabel => _localizationService.GetString("Customer_AllowFiles");
    public string SaveFileLabel => _localizationService.GetString("Customer_SaveFile");
    public string RejectFileLabel => _localizationService.GetString("Customer_RejectFile");
    public string SeeScreenLabel => _localizationService.GetString("Customer_SeeScreen");
    public string IncomingFileLabel => string.Format(_localizationService.GetString("Customer_IncomingFile"), IncomingFileName);
    public string AgentWantsToConnectText => string.Format(_localizationService.GetString("Customer_AgentWantsToConnect"), RequestingAgentName);
    public string ConnectedToFormatted => string.Format(_localizationService.GetString("Customer_ConnectedTo"), RequestingAgentName);
    public FlowDirection LayoutDirection =>
        _localizationService.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public event EventHandler<SessionAcceptedEventArgs>? OnSessionAccepted;
    public event EventHandler? OnSessionDisconnecting;

    public CustomerViewModel(
        ICustomerApiClient apiClient,
        ILocalizationService localizationService)
    {
        _apiClient = apiClient;
        _localizationService = localizationService;
        _deviceIdentifier = GetDeviceIdentifier();

        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    [RelayCommand]
    private async Task GenerateSupportCodeAsync()
    {
        IsBusy = true;
        ClearError();

        try
        {
            var result = await _apiClient.GenerateSupportCodeAsync(_deviceIdentifier);

            if (result.IsSuccess && result.Code is not null)
            {
                SupportCode = result.Code.Length == 8
                    ? result.Code.Insert(4, " ")
                    : result.Code;
                IsCodeActive = true;
                ConnectionState = ConnectionState.CodeActive;
                ConnectionStatusText = _localizationService.GetString("Customer_WaitingForAgent");
                ConnectionStatusColor = "#10B981";

                if (result.ExpiresAtUtc.HasValue)
                {
                    var remaining = result.ExpiresAtUtc.Value - DateTime.UtcNow;
                    CodeExpiry = string.Format(_localizationService.GetString("Customer_ExpiresIn"), (int)remaining.TotalMinutes);
                }

                StartPolling();
            }
            else
            {
                SupportCode = "----";
                IsCodeActive = false;
                ConnectionState = ConnectionState.Offline;
                ConnectionStatusText = _localizationService.GetString("Customer_Offline");
                ConnectionStatusColor = "#6B7280";
                SetError(result.ErrorMessage ?? _localizationService.GetString("Customer_CodeFailed"));
            }
        }
        catch (Exception)
        {
            SetError(_localizationService.GetString("Customer_Error_Generic"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AcceptConnectionAsync()
    {
        if (PendingSessionId == Guid.Empty) return;

        IsBusy = true;
        try
        {
            var success = await _apiClient.AcceptConnectionAsync(PendingSessionId);
            if (success)
            {
                StopPolling();
                ShowConnectionRequest = false;
                HasActiveSession = true;
                ShowActiveSessionPanel = true;
                ConnectionState = ConnectionState.ActiveSession;
                ConnectionStatusText = _localizationService.GetString("Customer_ConnectedToAgent");
                ConnectionStatusColor = "#10B981";

                OnSessionAccepted?.Invoke(this, new SessionAcceptedEventArgs
                {
                    SessionId = PendingSessionId,
                    AgentName = RequestingAgentName,
                    AgentRole = RequestingAgentRole
                });
            }
            else
            {
                SetError(_localizationService.GetString("Customer_AcceptFailed"));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RejectConnectionAsync()
    {
        if (PendingSessionId == Guid.Empty) return;

        IsBusy = true;
        try
        {
            await _apiClient.RejectConnectionAsync(PendingSessionId);
            ShowConnectionRequest = false;
            PendingSessionId = Guid.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            OnSessionDisconnecting?.Invoke(this, EventArgs.Empty);
            await _apiClient.DisconnectAsync();
            HasActiveSession = false;
            ShowActiveSessionPanel = false;
            ShowConnectionRequest = false;
            ConnectionState = ConnectionState.CodeActive;
            ConnectionStatusText = _localizationService.GetString("Customer_WaitingForAgent");
            ConnectionStatusColor = "#10B981";
            PendingSessionId = Guid.Empty;
            RequestingAgentName = string.Empty;
            StartPolling();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleLanguage()
    {
        var newLang = _localizationService.IsRtl ? "en" : "fa";
        _localizationService.SetLanguage(newLang);
    }

    private void StartPolling()
    {
        _pollTimer?.Dispose();
        _pollTimer = new Timer(async _ => await PollForConnectionRequestAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    public void NotifyRemoteSessionEnded()
    {
        HasActiveSession = false;
        ShowActiveSessionPanel = false;
        ShowConnectionRequest = false;
        ConnectionState = ConnectionState.CodeActive;
        ConnectionStatusText = _localizationService.GetString("Customer_WaitingForAgent");
        ConnectionStatusColor = "#10B981";
        StartPolling();
    }

    public void ShowIncomingRequest(Guid sessionId, string agentName, string agentRole)
    {
        PendingSessionId = sessionId;
        RequestingAgentName = string.IsNullOrWhiteSpace(agentName) ? "Support Agent" : agentName;
        RequestingAgentRole = string.IsNullOrWhiteSpace(agentRole) ? "Agent" : agentRole;
        ShowConnectionRequest = true;
        ConnectionState = ConnectionState.ConnectionRequested;
        ConnectionStatusText = _localizationService.GetString("Customer_ConnRequestTitle");
        ConnectionStatusColor = "#F59E0B";
    }

    private async Task PollForConnectionRequestAsync()
    {
        if (string.IsNullOrEmpty(SupportCode) || SupportCode == "---" || SupportCode == "----")
            return;

        try
        {
            var pollCode = new string(SupportCode.Where(char.IsDigit).ToArray());
            if (string.IsNullOrEmpty(pollCode))
                pollCode = SupportCode;
            var result = await _apiClient.CheckConnectionRequestAsync(pollCode);

            if (result.HasPendingRequest && result.SessionId.HasValue)
            {
                Application.Current?.Dispatcher.Invoke(() =>
                    ShowIncomingRequest(result.SessionId.Value, result.AgentName ?? "Support Agent", result.AgentRole ?? "Agent"));
            }
        }
        catch { }
    }

    private static string GetDeviceIdentifier()
    {
        try
        {
            return Environment.MachineName;
        }
        catch
        {
            return "customer-device";
        }
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(SupportIdLabel));
        OnPropertyChanged(nameof(ShareCodeInstruction));
        OnPropertyChanged(nameof(CodeExpiresLabel));
        OnPropertyChanged(nameof(RefreshCodeButton));
        OnPropertyChanged(nameof(ConnectionRequestTitle));
        OnPropertyChanged(nameof(AcceptButton));
        OnPropertyChanged(nameof(RejectButton));
        OnPropertyChanged(nameof(DisconnectButton));
        OnPropertyChanged(nameof(ActiveSessionLabel));
        OnPropertyChanged(nameof(ConnectedToLabel));
        OnPropertyChanged(nameof(SupportActiveWarning));
        OnPropertyChanged(nameof(ConnectedToAgentText));
        OnPropertyChanged(nameof(LanguageButton));
        OnPropertyChanged(nameof(BrandTagline));
        OnPropertyChanged(nameof(AllowRemoteLabel));
        OnPropertyChanged(nameof(AllowClipboardLabel));
        OnPropertyChanged(nameof(AllowFilesLabel));
        OnPropertyChanged(nameof(SaveFileLabel));
        OnPropertyChanged(nameof(RejectFileLabel));
        OnPropertyChanged(nameof(SeeScreenLabel));
        OnPropertyChanged(nameof(IncomingFileLabel));
        OnPropertyChanged(nameof(AgentWantsToConnectText));
        OnPropertyChanged(nameof(ConnectedToFormatted));
        OnPropertyChanged(nameof(LayoutDirection));
    }

    public void StopPolling()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }
}
