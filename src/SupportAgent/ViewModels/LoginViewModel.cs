using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SupportAgent.Models;
using SupportAgent.Services.Interfaces;
using RemoteSupport.Shared;

namespace SupportAgent.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly IApiClient _apiClient;
    private readonly ITokenStorage _tokenStorage;
    private readonly INavigationService _navigationService;
    private readonly ILocalizationService _localizationService;
    private readonly AgentOptions _agentOptions;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    private bool _rememberMe = true;

    [ObservableProperty]
    private string _serverStatus = "Checking server...";

    [ObservableProperty]
    private string _serverStatusColor = "#F59E0B";

    [ObservableProperty]
    private string _serverStatusDetail = "";

    public event Action<UserSession>? LoginSuccess;

    public string LoginTitle => _localizationService.GetString("Login_Title");
    public string LoginSubtitle => _localizationService.GetString("Login_Subtitle");
    public string UsernameLabel => _localizationService.GetString("Login_Username");
    public string PasswordLabel => _localizationService.GetString("Login_Password");
    public string ServerLabel => _localizationService.GetString("Login_Server");
    public string LoginButton => _localizationService.GetString("Login_Button");
    public string RememberMeLabel => _localizationService.GetString("Login_RememberMe");
    public string LoginForgotPassword => _localizationService.GetString("Login_ForgotPassword");
    public string LanguageButton => _localizationService.IsRtl ? "English" : "فارسی";
    public string BrandTagline => _localizationService.GetString("Login_BrandTagline");
    public FlowDirection LayoutDirection =>
        _localizationService.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public LoginViewModel(
        IApiClient apiClient,
        ITokenStorage tokenStorage,
        INavigationService navigationService,
        ILocalizationService localizationService,
        AgentOptions agentOptions)
    {
        _apiClient = apiClient;
        _tokenStorage = tokenStorage;
        _navigationService = navigationService;
        _localizationService = localizationService;
        _agentOptions = agentOptions;
        _serverUrl = agentOptions.ServerUrl;

        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public async Task CheckServerConnectionAsync()
    {
        ServerStatus = "Checking server...";
        ServerStatusColor = "#F59E0B";
        ServerStatusDetail = "";

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync($"{_agentOptions.ServerUrl.TrimEnd('/')}/health");
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                ServerStatus = "Server: Connected";
                ServerStatusColor = "#10B981";
                ServerStatusDetail = $"{_agentOptions.ServerUrl} | {response.StatusCode}";
            }
            else
            {
                ServerStatus = "Server: Responded with error";
                ServerStatusColor = "#EF4444";
                ServerStatusDetail = $"{response.StatusCode} {response.ReasonPhrase}";
            }
        }
        catch (HttpRequestException ex)
        {
            ServerStatus = "Server: NOT reachable";
            ServerStatusColor = "#EF4444";
            ServerStatusDetail = $"{_agentOptions.ServerUrl} - {ex.InnerException?.Message ?? ex.Message}";
        }
        catch (TaskCanceledException)
        {
            ServerStatus = "Server: Timeout";
            ServerStatusColor = "#EF4444";
            ServerStatusDetail = "Connection timed out (5s)";
        }
        catch (Exception ex)
        {
            ServerStatus = "Server: Error";
            ServerStatusColor = "#EF4444";
            ServerStatusDetail = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            SetError(_localizationService.GetString("Login_Validation_Error"));
            return;
        }

        IsBusy = true;
        ClearError();

        try
        {
            var result = await _apiClient.LoginAsync(Username, Password);

            if (result.IsSuccess && result.AccessToken is not null && result.RefreshToken is not null)
            {
            if (RememberMe)
                {
                    _tokenStorage.SaveTokens(
                        result.AccessToken,
                        result.RefreshToken,
                        result.AccessTokenExpiresAtUtc,
                        result.RefreshTokenExpiresAtUtc,
                        Username);
                }
                else
                {
                    _tokenStorage.ClearTokens();
                }

                var session = new UserSession
                {
                    UserId = result.UserId ?? Guid.Empty,
                    Username = result.Username ?? Username,
                    Email = result.Email ?? string.Empty,
                    Role = result.Role ?? string.Empty,
                    DisplayName = result.DisplayName,
                    AccessToken = result.AccessToken,
                    RefreshToken = result.RefreshToken,
                    AccessTokenExpiresAtUtc = result.AccessTokenExpiresAtUtc,
                    RefreshTokenExpiresAtUtc = result.RefreshTokenExpiresAtUtc
                };

                LoginSuccess?.Invoke(session);
            }
            else
            {
                SetError(result.ErrorMessage ?? _localizationService.GetString("Login_Failed"));
            }
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
    private void ToggleLanguage()
    {
        var newLang = _localizationService.IsRtl ? "en" : "fa";
        _localizationService.SetLanguage(newLang);
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(LoginTitle));
        OnPropertyChanged(nameof(LoginSubtitle));
        OnPropertyChanged(nameof(UsernameLabel));
        OnPropertyChanged(nameof(PasswordLabel));
        OnPropertyChanged(nameof(ServerLabel));
        OnPropertyChanged(nameof(LoginButton));
        OnPropertyChanged(nameof(RememberMeLabel));
        OnPropertyChanged(nameof(LanguageButton));
        OnPropertyChanged(nameof(BrandTagline));
        OnPropertyChanged(nameof(LayoutDirection));
    }

    public string? TryPeekUsername()
        => _tokenStorage.LoadTokens()?.Username;

    public async Task<UserSession?> TryRestoreSessionAsync()
    {
        var tokens = _tokenStorage.LoadTokens();
        if (tokens is null) return null;

        var (accessToken, refreshToken, accessExpiry, refreshExpiry, username) = tokens.Value;
        Username = username ?? Username;
        if (string.IsNullOrEmpty(refreshToken)) return null;

        try
        {
            if (accessExpiry <= DateTime.UtcNow)
            {
                if (refreshExpiry <= DateTime.UtcNow)
                {
                    _tokenStorage.ClearTokens();
                    return null;
                }

                var refreshed = await _apiClient.RefreshTokenAsync(refreshToken);
                if (!refreshed.IsSuccess || refreshed.AccessToken is null || refreshed.RefreshToken is null)
                {
                    _tokenStorage.ClearTokens();
                    return null;
                }

                accessToken = refreshed.AccessToken;
                refreshToken = refreshed.RefreshToken;
                accessExpiry = refreshed.AccessTokenExpiresAtUtc;
                refreshExpiry = refreshed.RefreshTokenExpiresAtUtc;
                Username = refreshed.Username ?? Username;
            }

            _apiClient.SetAccessToken(accessToken);
            var me = await _apiClient.GetCurrentUserAsync();
            if (me is null)
            {
                var refreshed = await _apiClient.RefreshTokenAsync(refreshToken);
                if (!refreshed.IsSuccess || refreshed.AccessToken is null)
                {
                    _tokenStorage.ClearTokens();
                    return null;
                }

                accessToken = refreshed.AccessToken;
                refreshToken = refreshed.RefreshToken ?? refreshToken;
                accessExpiry = refreshed.AccessTokenExpiresAtUtc;
                refreshExpiry = refreshed.RefreshTokenExpiresAtUtc;
                _apiClient.SetAccessToken(accessToken);
                me = await _apiClient.GetCurrentUserAsync();
            }

            if (me is null)
                return null;

            if (RememberMe)
                _tokenStorage.SaveTokens(accessToken!, refreshToken, accessExpiry, refreshExpiry, me.Username);

            return new UserSession
            {
                UserId = me.Id,
                Username = me.Username,
                Email = me.Email,
                Role = me.Role,
                DisplayName = me.DisplayName,
                AccessToken = accessToken ?? string.Empty,
                RefreshToken = refreshToken,
                AccessTokenExpiresAtUtc = accessExpiry,
                RefreshTokenExpiresAtUtc = refreshExpiry
            };
        }
        catch
        {
            return null;
        }
    }
}
