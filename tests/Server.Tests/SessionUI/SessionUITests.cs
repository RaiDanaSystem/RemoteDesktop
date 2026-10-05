using RemoteSupport.Shared.RemoteInput;
using RemoteSupport.Shared.Clipboard;
using SupportAgent.Services.Interfaces;
using SupportAgent.ViewModels.Session;
using CustomerAgent.ViewModels.Session;

namespace RemoteSupport.Server.Tests.SessionUI;

// Local interface matching CustomerAgent.Services.Interfaces.ILocalizationService
public interface ICustomerLocalizationService
{
    string CurrentLanguage { get; }
    bool IsRtl { get; }
    event Action? LanguageChanged;
    void SetLanguage(string language);
    string GetString(string key);
}

public class SessionUITests : IDisposable
{
    private readonly InputConsentManager _consentManager;
    private readonly ClipboardManager _clipboardManager;
    private readonly TestLocalizationService _localization;

    public SessionUITests()
    {
        _consentManager = new InputConsentManager();
        _clipboardManager = new ClipboardManager();
        _localization = new TestLocalizationService();
    }

    public void Dispose() { }

    // --- Support Agent SessionViewModel Tests ---

    [Fact]
    public void SessionViewModel_Initial_State()
    {
        var vm = new SessionViewModel(_localization);

        Assert.False(vm.IsConnected);
        Assert.False(vm.IsScreenSharing);
        Assert.True(vm.IsMouseControlEnabled);
        Assert.False(vm.IsKeyboardControlEnabled);
        Assert.True(vm.IsClipboardEnabled);
        Assert.Equal("Disconnected", vm.ConnectionStatus);
    }

    [Fact]
    public void SessionViewModel_StartSession_SetsState()
    {
        var vm = new SessionViewModel(_localization);

        vm.StartSession("session-1", "Customer PC");

        Assert.True(vm.IsConnected);
        Assert.True(vm.IsScreenSharing);
        Assert.True(vm.IsMouseControlEnabled);
        Assert.Equal("session-1", vm.SessionId);
        Assert.Equal("Customer PC", vm.CustomerDevice);
    }

    [Fact]
    public void SessionViewModel_ToggleMouseControl()
    {
        var vm = new SessionViewModel(_localization);
        vm.StartSession("session-1", "PC");

        vm.ToggleMouseControlCommand.Execute(null);
        Assert.False(vm.IsMouseControlEnabled);

        vm.ToggleMouseControlCommand.Execute(null);
        Assert.True(vm.IsMouseControlEnabled);
    }

    [Fact]
    public void SessionViewModel_ToggleKeyboardControl()
    {
        var vm = new SessionViewModel(_localization);

        vm.ToggleKeyboardControlCommand.Execute(null);
        Assert.True(vm.IsKeyboardControlEnabled);

        vm.ToggleKeyboardControlCommand.Execute(null);
        Assert.False(vm.IsKeyboardControlEnabled);
    }

    [Fact]
    public void SessionViewModel_ToggleClipboard()
    {
        var vm = new SessionViewModel(_localization);

        Assert.True(vm.IsClipboardEnabled);

        vm.ToggleClipboardCommand.Execute(null);
        Assert.False(vm.IsClipboardEnabled);

        vm.ToggleClipboardCommand.Execute(null);
        Assert.True(vm.IsClipboardEnabled);
    }

    [Fact]
    public void SessionViewModel_ToggleMute()
    {
        var vm = new SessionViewModel(_localization);

        vm.ToggleMuteCommand.Execute(null);
        Assert.True(vm.IsMuted);

        vm.ToggleMuteCommand.Execute(null);
        Assert.False(vm.IsMuted);
    }

    [Fact]
    public void SessionViewModel_ToggleDisplayMode()
    {
        var vm = new SessionViewModel(_localization);

        Assert.Equal("FitToScreen", vm.DisplayMode);

        vm.ToggleActualSizeCommand.Execute(null);
        Assert.Equal("ActualSize", vm.DisplayMode);

        vm.ToggleFitToScreenCommand.Execute(null);
        Assert.Equal("FitToScreen", vm.DisplayMode);
    }

    [Fact]
    public void SessionViewModel_SendChatMessage()
    {
        var vm = new SessionViewModel(_localization);

        vm.ChatInput = "Hello customer!";
        vm.SendChatMessageCommand.Execute(null);

        Assert.Single(vm.ChatMessages);
        Assert.Equal("Hello customer!", vm.ChatMessages[0].Content);
        Assert.Equal("You", vm.ChatMessages[0].SenderName);
        Assert.True(vm.ChatMessages[0].IsOwnMessage);
        Assert.Equal(string.Empty, vm.ChatInput);
    }

    [Fact]
    public void SessionViewModel_SendChatMessage_Empty_Ignored()
    {
        var vm = new SessionViewModel(_localization);

        vm.ChatInput = "";
        vm.SendChatMessageCommand.Execute(null);

        Assert.Empty(vm.ChatMessages);

        vm.ChatInput = "   ";
        vm.SendChatMessageCommand.Execute(null);

        Assert.Empty(vm.ChatMessages);
    }

    [Fact]
    public void SessionViewModel_ChatHistory_PersistsMultipleMessages()
    {
        var vm = new SessionViewModel(_localization);

        for (int i = 0; i < 10; i++)
        {
            vm.ChatInput = $"Message {i}";
            vm.SendChatMessageCommand.Execute(null);
        }

        Assert.Equal(10, vm.ChatMessages.Count);
    }

    // --- Customer Agent CustomerSessionViewModel Tests ---

    [Fact]
    public void CustomerSessionViewModel_Initial_State()
    {
        var vm = new CustomerSessionViewModel(_localization);

        Assert.False(vm.IsActiveSession);
        Assert.False(vm.ScreenSharingActive);
        Assert.False(vm.RemoteControlEnabled);
        Assert.False(vm.ClipboardEnabled);
        Assert.False(vm.FileTransferEnabled);
        Assert.False(vm.AudioEnabled);
    }

    [Fact]
    public void CustomerSessionViewModel_StartSession()
    {
        var vm = new CustomerSessionViewModel(_localization);

        vm.StartSession("Agent Smith", "SupportAgent");

        Assert.True(vm.IsActiveSession);
        Assert.Equal("Agent Smith", vm.AgentName);
        Assert.Equal("SupportAgent", vm.AgentRole);
        Assert.True(vm.ScreenSharingActive);
        Assert.Contains("Agent Smith", vm.ConnectionStatus);
    }

    [Fact]
    public void CustomerSessionViewModel_EndSession()
    {
        var vm = new CustomerSessionViewModel(_localization);
        vm.StartSession("Agent", "SupportAgent");

        vm.EndSessionCommand.Execute(null);

        Assert.False(vm.IsActiveSession);
        Assert.False(vm.ScreenSharingActive);
        Assert.False(vm.RemoteControlEnabled);
    }

    [Fact]
    public void CustomerSessionViewModel_PermissionLabels()
    {
        var vm = new CustomerSessionViewModel(_localization);

        Assert.Contains("Disabled", vm.RemoteControlLabel);
        Assert.Contains("Blocked", vm.ClipboardLabel);
        Assert.Contains("Blocked", vm.FileTransferLabel);
        Assert.Contains("Muted", vm.AudioLabel);
    }

    [Fact]
    public void CustomerSessionViewModel_PermissionLabels_WhenEnabled()
    {
        var vm = new CustomerSessionViewModel(_localization);

        vm.RemoteControlEnabled = true;
        vm.ClipboardEnabled = true;
        vm.FileTransferEnabled = true;
        vm.AudioEnabled = true;

        Assert.Contains("Enabled", vm.RemoteControlLabel);
        Assert.Contains("Allowed", vm.ClipboardLabel);
        Assert.Contains("Allowed", vm.FileTransferLabel);
        Assert.Contains("Active", vm.AudioLabel);
    }

    [Fact]
    public void CustomerSessionViewModel_SessionDuration()
    {
        var vm = new CustomerSessionViewModel(_localization);

        vm.StartSession("Agent", "SupportAgent");

        Assert.Matches(@"\d{2}:\d{2}:\d{2}", vm.SessionDuration);
    }

    // --- Localization Tests ---

    [Fact]
    public void Localization_SetLanguage_SwitchesStrings()
    {
        _localization.SetLanguage("fa");

        Assert.Equal("fa", _localization.CurrentLanguage);
        Assert.True(_localization.IsRtl);

        _localization.SetLanguage("en");
        Assert.Equal("en", _localization.CurrentLanguage);
        Assert.False(_localization.IsRtl);
    }

    [Fact]
    public void Localization_LanguageChanged_Event()
    {
        bool changed = false;
        _localization.LanguageChanged += () => changed = true;

        _localization.SetLanguage("fa");

        Assert.True(changed);
    }

    // --- Consent Integration Tests ---

    [Fact]
    public void ConsentManager_SessionIsolation()
    {
        _consentManager.GrantConsent("session-A");

        Assert.True(_consentManager.ValidateConsent("session-A"));
        Assert.False(_consentManager.ValidateConsent("session-B"));
    }

    [Fact]
    public void ClipboardManager_SessionIsolation()
    {
        _clipboardManager.GrantConsent("session-A");

        Assert.True(_clipboardManager.ValidateConsent("session-A"));
        Assert.False(_clipboardManager.ValidateConsent("session-B"));
    }

    // --- Multiple Chat Messages Test ---

    [Fact]
    public void SessionViewModel_MultipleChatsFromAgent()
    {
        var vm = new SessionViewModel(_localization);

        vm.ChatInput = "Agent: How can I help?";
        vm.SendChatMessageCommand.Execute(null);

        vm.ChatInput = "Agent: Let me check";
        vm.SendChatMessageCommand.Execute(null);

        Assert.Equal(2, vm.ChatMessages.Count);
        Assert.True(vm.ChatMessages[0].IsOwnMessage);
        Assert.True(vm.ChatMessages[1].IsOwnMessage);
    }
}

// Test double for localization - implements both SupportAgent and CustomerAgent interfaces
public class TestLocalizationService : ILocalizationService, ICustomerLocalizationService, CustomerAgent.Services.Interfaces.ILocalizationService
{
    private string _currentLanguage = "en";
    public string CurrentLanguage => _currentLanguage;
    public bool IsRtl => _currentLanguage == "fa";
    public event Action? LanguageChanged;

    public void SetLanguage(string language)
    {
        _currentLanguage = language;
        LanguageChanged?.Invoke();
    }

    public string GetString(string key)
    {
        return _currentLanguage switch
        {
            "fa" => key switch
            {
                "Dashboard_Connected" => "متصل شد",
                "Session_Timer" => "مدت زمان: {0}",
                "Session_FPS" => "فریم: {0}",
                "Session_Bitrate" => "نرخ بیت: {0} کیلوبیت",
                "Session_Latency" => "تأخیر: {0} میلی‌ثانیه",
                "Customer_RemoteControl" => "کنترل از راه دور: {0}",
                "Customer_ClipboardAccess" => "دسترسی کلیپ‌بورد: {0}",
                "Customer_FileTransfer" => "انتقال فایل: {0}",
                "Customer_AudioAccess" => "صدا: {0}",
                _ => key
            },
            _ => key switch
            {
                "Dashboard_Connected" => "Connected",
                "Session_Timer" => "Duration: {0}",
                "Session_FPS" => "FPS: {0}",
                "Session_Bitrate" => "Bitrate: {0} kbps",
                "Session_Latency" => "Latency: {0}ms",
                "Customer_RemoteControl" => "Remote control is {0}",
                "Customer_ClipboardAccess" => "Clipboard access: {0}",
                "Customer_FileTransfer" => "File transfer: {0}",
                "Customer_AudioAccess" => "Audio: {0}",
                _ => key
            }
        };
    }
}
