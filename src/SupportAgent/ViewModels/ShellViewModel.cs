using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CustomerAgent;
using CustomerAgent.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteSupport.Shared;
using SupportAgent.Configuration;
using SupportAgent.Models;
using SupportAgent.Services.Interfaces;
using SupportAgent.Views;

namespace SupportAgent.ViewModels;

public partial class ShellViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly ServerEndpointStore _endpointStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _appSettingsUrl;
    private readonly string _supportUsername;
    private readonly string _supportPassword;

    private Action? _customerLanguageSync;
    private IServiceProvider? _modeServices;
    private SupportWorkspace? _supportWorkspace;
    private CustomerWorkspace? _customerWorkspace;

    [ObservableProperty] private object? _modeContent;
    [ObservableProperty] private bool _isHome = true;
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private string _homeServerUrl = string.Empty;
    [ObservableProperty] private string _settingsServerUrl = string.Empty;
    [ObservableProperty] private string _modeTitle = string.Empty;

    public bool ShowServerOnHome => EndpointResolver.ShowUrlOnHome;
    public string LanguageButton => _localization.IsRtl ? "English" : "فارسی";
    public FlowDirection LayoutDirection =>
        _localization.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public string AppTitle => _localization.GetString("Shell_AppTitle");
    public string HomeHeadline => _localization.GetString("Shell_Headline");
    public string HomeSubhead => _localization.GetString("Shell_Subhead");
    public string SupportCardTitle => _localization.GetString("Shell_SupportTitle");
    public string SupportCardBody => _localization.GetString("Shell_SupportBody");
    public string CustomerCardTitle => _localization.GetString("Shell_CustomerTitle");
    public string CustomerCardBody => _localization.GetString("Shell_CustomerBody");
    public string ServerLabel => _localization.GetString("Shell_ServerLabel");
    public string ServerHint => _localization.GetString("Shell_ServerHint");
    public string SaveServerButton => _localization.GetString("Shell_SaveServer");
    public string SettingsTitle => _localization.GetString("Shell_SettingsTitle");
    public string SettingsServerHint => _localization.GetString("Shell_SettingsServerHint");
    public string BackButton => _localization.GetString("Shell_Back");
    public string SettingsButton => _localization.GetString("Dashboard_Settings");

    public ShellViewModel(
        ILocalizationService localization,
        ServerEndpointStore endpointStore,
        ILoggerFactory loggerFactory,
        AgentOptions bootstrapOptions)
    {
        _localization = localization;
        _endpointStore = endpointStore;
        _loggerFactory = loggerFactory;
        _appSettingsUrl = bootstrapOptions.ServerUrl;
        _supportUsername = string.IsNullOrWhiteSpace(bootstrapOptions.SupportUsername)
            ? "admin"
            : bootstrapOptions.SupportUsername;
        _supportPassword = string.IsNullOrWhiteSpace(bootstrapOptions.SupportPassword)
            ? "Admin@12345"
            : bootstrapOptions.SupportPassword;

        _localization.LanguageChanged += RefreshLanguage;
        HomeServerUrl = EndpointResolver.ShowUrlOnHome
            ? EndpointResolver.Resolve(_appSettingsUrl, _endpointStore.LoadOverride())
            : string.Empty;
        SettingsServerUrl = EndpointResolver.SettingsEditorValue(_endpointStore.LoadOverride());
    }

    public string CurrentServerUrl =>
        EndpointResolver.Resolve(_appSettingsUrl, _endpointStore.LoadOverride());

    [RelayCommand]
    private async Task EnterSupportAsync()
    {
        if (!IsHome)
            return;

        PersistHomeUrlIfVisible();
        IsBusy = true;
        ClearError();
        try
        {
            var options = CreateOptions();
            var services = new ServiceCollection();
            services.AddSingleton(_loggerFactory);
            SupportComposition.ConfigureServices(services, options, _localization);
            var provider = services.BuildServiceProvider();

            var api = provider.GetRequiredService<IApiClient>();
            var login = await api.LoginAsync(_supportUsername, _supportPassword);
            if (!login.IsSuccess || login.AccessToken is null || login.RefreshToken is null)
            {
                await DisposeProviderAsync(provider);
                SetError(login.ErrorMessage ?? _localization.GetString("Login_Failed"));
                return;
            }

            var session = new UserSession
            {
                UserId = login.UserId ?? Guid.Empty,
                Username = login.Username ?? _supportUsername,
                Email = login.Email ?? string.Empty,
                Role = login.Role ?? string.Empty,
                DisplayName = login.DisplayName,
                AccessToken = login.AccessToken,
                RefreshToken = login.RefreshToken,
                AccessTokenExpiresAtUtc = login.AccessTokenExpiresAtUtc,
                RefreshTokenExpiresAtUtc = login.RefreshTokenExpiresAtUtc
            };

            _modeServices = provider;
            _supportWorkspace = new SupportWorkspace(provider, session);
            ModeContent = _supportWorkspace;
            IsHome = false;
            ModeTitle = _localization.GetString("Shell_SupportTitle");
        }
        catch (Exception ex)
        {
            SetError($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EnterCustomerAsync()
    {
        if (!IsHome)
            return;

        PersistHomeUrlIfVisible();
        IsBusy = true;
        ClearError();
        try
        {
            var options = CreateOptions();
            var services = new ServiceCollection();
            services.AddSingleton(_loggerFactory);
            CustomerComposition.ConfigureServices(services, options);
            var provider = services.BuildServiceProvider();
            var customerLoc = provider.GetRequiredService<CustomerAgent.Services.Interfaces.ILocalizationService>();
            customerLoc.SetLanguage(_localization.CurrentLanguage);
            _customerLanguageSync = () => customerLoc.SetLanguage(_localization.CurrentLanguage);
            _localization.LanguageChanged += _customerLanguageSync;
            _modeServices = provider;
            _customerWorkspace = new CustomerWorkspace(provider);
            ModeContent = _customerWorkspace;
            IsHome = false;
            ModeTitle = _localization.GetString("Shell_CustomerTitle");
        }
        catch (Exception ex)
        {
            SetError($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task GoHomeAsync()
    {
        if (IsHome)
            return;

        var live = _supportWorkspace?.HasLiveSession == true
                   || _customerWorkspace?.HasLiveSession == true;
        if (live)
        {
            var result = MessageBox.Show(
                _localization.GetString("Shell_LeaveSessionWarning"),
                _localization.GetString("Shell_LeaveSessionTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;
        }

        await LeaveModeAsync();
    }

    public async Task LeaveModeAsync()
    {
        try
        {
            if (_supportWorkspace is not null)
                await _supportWorkspace.StopAsync();
            if (_customerWorkspace is not null)
                await _customerWorkspace.StopAsync();
        }
        catch { }

        if (_customerLanguageSync is not null)
        {
            _localization.LanguageChanged -= _customerLanguageSync;
            _customerLanguageSync = null;
        }

        _supportWorkspace = null;
        _customerWorkspace = null;
        ModeContent = null;
        IsHome = true;
        ModeTitle = string.Empty;
        await DisposeProviderAsync(_modeServices);
        _modeServices = null;
    }

    [RelayCommand]
    private void ToggleLanguage()
    {
        _localization.SetLanguage(_localization.IsRtl ? "en" : "fa");
    }

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsServerUrl = EndpointResolver.SettingsEditorValue(_endpointStore.LoadOverride());
        ShowSettings = true;
    }

    [RelayCommand]
    private void CloseSettings() => ShowSettings = false;

    [RelayCommand]
    private void SaveHomeServer()
    {
        PersistHomeUrlIfVisible();
        StatusMessage = _localization.GetString("Shell_ServerSaved");
        HasError = false;
    }

    [RelayCommand]
    private void SaveSettingsServer()
    {
        if (string.IsNullOrWhiteSpace(SettingsServerUrl))
        {
            _endpointStore.ClearOverride();
        }
        else
        {
            _endpointStore.SaveOverride(EndpointResolver.Normalize(SettingsServerUrl));
        }

        if (ShowServerOnHome)
            HomeServerUrl = CurrentServerUrl;

        ShowSettings = false;
        StatusMessage = _localization.GetString("Shell_ServerSaved");
        HasError = false;
    }

    private void PersistHomeUrlIfVisible()
    {
        if (!ShowServerOnHome)
            return;
        if (string.IsNullOrWhiteSpace(HomeServerUrl))
            return;
        _endpointStore.SaveOverride(EndpointResolver.Normalize(HomeServerUrl));
    }

    private AgentOptions CreateOptions() => new()
    {
        ServerUrl = CurrentServerUrl,
        SupportUsername = _supportUsername,
        SupportPassword = _supportPassword
    };

    private static async Task DisposeProviderAsync(IServiceProvider? provider)
    {
        if (provider is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (provider is IDisposable disposable)
            disposable.Dispose();
    }

    private void RefreshLanguage()
    {
        OnPropertyChanged(nameof(LanguageButton));
        OnPropertyChanged(nameof(LayoutDirection));
        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(HomeHeadline));
        OnPropertyChanged(nameof(HomeSubhead));
        OnPropertyChanged(nameof(SupportCardTitle));
        OnPropertyChanged(nameof(SupportCardBody));
        OnPropertyChanged(nameof(CustomerCardTitle));
        OnPropertyChanged(nameof(CustomerCardBody));
        OnPropertyChanged(nameof(ServerLabel));
        OnPropertyChanged(nameof(ServerHint));
        OnPropertyChanged(nameof(SaveServerButton));
        OnPropertyChanged(nameof(SettingsTitle));
        OnPropertyChanged(nameof(SettingsServerHint));
        OnPropertyChanged(nameof(BackButton));
        OnPropertyChanged(nameof(SettingsButton));
        if (!IsHome)
            ModeTitle = _supportWorkspace is not null
                ? _localization.GetString("Shell_SupportTitle")
                : _localization.GetString("Shell_CustomerTitle");
        OnPropertyChanged(nameof(ModeTitle));
    }
}
