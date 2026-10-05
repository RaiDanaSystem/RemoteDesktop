using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteSupport.Shared.RemoteInput;
using RemoteSupport.Shared.ScreenStreaming;
using SupportAgent.Services.Interfaces;
using SupportAgent.Services.Session;

namespace SupportAgent.ViewModels.Session;

public partial class SessionViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private RemoteDesktopSession? _session;
    private System.Windows.Threading.DispatcherTimer? _diagnosticsTimer;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isScreenSharing;
    [ObservableProperty] private bool _isMouseControlEnabled = true;
    [ObservableProperty] private bool _isShowRemoteCursor;
    [ObservableProperty] private bool _isKeyboardControlEnabled = true;
    [ObservableProperty] private bool _isClipboardEnabled = true;
    [ObservableProperty] private bool _isAudioEnabled;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private int _volume = 80;
    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private string _connectionStatusColor = "#EF4444";
    [ObservableProperty] private string _sessionId = string.Empty;
    [ObservableProperty] private string _customerDevice = string.Empty;
    [ObservableProperty] private string _sessionDuration = "00:00:00";
    [ObservableProperty] private string _currentMonitor = "Primary";
    [ObservableProperty] private string _displayMode = "FitToScreen";

    // Remote screen image
    [ObservableProperty] private ImageSource? _remoteScreenImage;

    public bool ShowConnectingOverlay => IsConnected && RemoteScreenImage is null;
    public bool ShowIdleOverlay => !IsConnected && RemoteScreenImage is null;

    // Diagnostics
    [ObservableProperty] private double _fps;
    [ObservableProperty] private long _bitrate;
    [ObservableProperty] private double _latency;
    [ObservableProperty] private int _droppedFrames;
    [ObservableProperty] private string _audioLevel = "0%";
    [ObservableProperty] private string _transportState = "Disconnected";
    [ObservableProperty] private int _streamFps = 20;
    [ObservableProperty] private int _streamQuality = 55;

    public int[] FpsOptions { get; } = [8, 10, 12, 15, 20, 24, 30, 45, 60];
    public int[] QualityOptions { get; } = [25, 35, 45, 55, 65, 75, 80];

    // Output resolution requested from the remote PC (Auto follows quality; 4K needs a fast PC and network)
    public string[] ResolutionOptions { get; } = ["Auto", "1080p", "1440p", "4K"];
    [ObservableProperty] private string _streamResolution = "Auto";
    private static int ResolutionToWidth(string value) => value switch
    {
        "1080p" => 1920,
        "1440p" => 2560,
        "4K" => 3840,
        _ => 0
    };

    // Chat
    [ObservableProperty] private string _chatInput = string.Empty;
    [ObservableProperty] private ObservableCollection<ChatMessageItem> _chatMessages = new();

    // File Transfer
    [ObservableProperty] private ObservableCollection<FileTransferItem> _activeTransfers = new();
    [ObservableProperty] private bool _hasActiveTransfers;

    private DateTime _sessionStartUtc;
    private System.Windows.Threading.DispatcherTimer? _sessionTimer;

    public event Func<Task>? SessionTeardownRequested;

    private System.Windows.Window? _remoteFullscreenWindow;

    public SessionViewModel(ILocalizationService localization)
    {
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Binds the ViewModel to a live RemoteDesktopSession.
    /// </summary>
    public void AttachSession(RemoteDesktopSession session)
    {
        DetachSession();
        _session = session;
        _session.FrameRendered += OnFrameRendered;
        _session.SessionEnded += OnSessionEnded;
        _session.LogMessage += OnLogMessage;
        _session.FileTransferProgress += OnFileTransferProgress;
        _ = _session.SendStreamSettingsAsync(StreamFps, StreamQuality, ResolutionToWidth(StreamResolution));
    }

    public void DetachSession()
    {
        if (_session is null)
            return;

        _session.FrameRendered -= OnFrameRendered;
        _session.SessionEnded -= OnSessionEnded;
        _session.LogMessage -= OnLogMessage;
        _session.FileTransferProgress -= OnFileTransferProgress;
        _session = null;
    }

    partial void OnIsMouseControlEnabledChanged(bool value) => _session?.SetMouseCapture(value);
    partial void OnIsShowRemoteCursorChanged(bool value) => _ = _session?.SetShowRemoteCursorAsync(value);
    partial void OnIsKeyboardControlEnabledChanged(bool value) => _session?.SetKeyboardCapture(value);
    partial void OnIsClipboardEnabledChanged(bool value) => _session?.SetClipboardSync(value);
    partial void OnStreamFpsChanged(int value) => _ = _session?.SendStreamSettingsAsync(value, StreamQuality);
    partial void OnStreamQualityChanged(int value) => _ = _session?.SendStreamSettingsAsync(StreamFps, value);
    partial void OnStreamResolutionChanged(string value) =>
        _ = _session?.SendStreamSettingsAsync(StreamFps, StreamQuality, ResolutionToWidth(value));
    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowConnectingOverlay));
        OnPropertyChanged(nameof(ShowIdleOverlay));
    }
    partial void OnRemoteScreenImageChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(ShowConnectingOverlay));
        OnPropertyChanged(nameof(ShowIdleOverlay));
    }

    // Commands
    [RelayCommand]
    private void ToggleFullscreen()
    {
        if (_remoteFullscreenWindow is not null)
        {
            _remoteFullscreenWindow.Close();
            return;
        }

        var image = new System.Windows.Controls.Image
        {
            Stretch = System.Windows.Media.Stretch.Uniform,
            Cursor = System.Windows.Input.Cursors.Arrow,
            Focusable = true,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(
            image, System.Windows.Media.BitmapScalingMode.HighQuality);
        image.SetBinding(
            System.Windows.Controls.Image.SourceProperty,
            new System.Windows.Data.Binding(nameof(RemoteScreenImage)) { Source = this });

        var bannerText = new System.Windows.Controls.TextBlock
        {
            Text = _localization.GetString("Session_FullscreenBanner"),
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 14,
            FontWeight = System.Windows.FontWeights.SemiBold,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            TextWrapping = System.Windows.TextWrapping.Wrap
        };

        var exitButton = new System.Windows.Controls.Button
        {
            Content = _localization.GetString("Session_ExitFullscreen"),
            Padding = new System.Windows.Thickness(14, 6, 14, 6),
            Margin = new System.Windows.Thickness(12, 0, 0, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new System.Windows.Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontWeight = System.Windows.FontWeights.SemiBold
        };

        var banner = new System.Windows.Controls.DockPanel
        {
            LastChildFill = true,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x4E, 0xD8))
        };
        banner.Children.Add(exitButton);
        System.Windows.Controls.DockPanel.SetDock(exitButton, System.Windows.Controls.Dock.Right);
        banner.Children.Add(bannerText);
        banner.Margin = new System.Windows.Thickness(0);
        banner.Height = 44;
        banner.SetValue(System.Windows.Controls.DockPanel.LastChildFillProperty, true);
        exitButton.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        bannerText.Margin = new System.Windows.Thickness(16, 0, 8, 0);

        var root = new System.Windows.Controls.Grid();
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        System.Windows.Controls.Grid.SetRow(banner, 0);
        System.Windows.Controls.Grid.SetRow(image, 1);
        root.Children.Add(banner);
        root.Children.Add(image);

        var mainWindow = System.Windows.Application.Current?.MainWindow;

        var window = new System.Windows.Window
        {
            Title = _localization.GetString("Session_Fullscreen"),
            WindowStyle = System.Windows.WindowStyle.None,
            WindowState = System.Windows.WindowState.Maximized,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.Black,
            Content = root,
            Owner = mainWindow,
            FlowDirection = _localization.IsRtl
                ? System.Windows.FlowDirection.RightToLeft
                : System.Windows.FlowDirection.LeftToRight
        };

        void CloseFullscreen()
        {
            window.Close();
        }

        exitButton.Click += (_, _) => CloseFullscreen();
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                CloseFullscreen();
            }
        };
        window.Closed += (_, _) =>
        {
            _remoteFullscreenWindow = null;
            var restore = (mainWindow as SupportAgent.Views.ShellWindow)?.ScreenCaptureElement;
            if (restore is not null)
                _session?.SetCaptureTarget(restore);
        };

        _remoteFullscreenWindow = window;
        window.Show();
        window.Activate();
        image.Focus();
        _session?.SetCaptureTarget(image);
    }
    [RelayCommand] private void ToggleFitToScreen() { DisplayMode = "FitToScreen"; }
    [RelayCommand] private void ToggleActualSize() { DisplayMode = "ActualSize"; }

    [RelayCommand]
    private void ToggleMouseControl()
    {
        IsMouseControlEnabled = !IsMouseControlEnabled;
        _session?.SetMouseCapture(IsMouseControlEnabled);
    }

    [RelayCommand]
    private void ToggleKeyboardControl()
    {
        IsKeyboardControlEnabled = !IsKeyboardControlEnabled;
        _session?.SetKeyboardCapture(IsKeyboardControlEnabled);
    }

    [RelayCommand]
    private void ToggleClipboard()
    {
        IsClipboardEnabled = !IsClipboardEnabled;
        _session?.SetClipboardSync(IsClipboardEnabled);
    }

    [RelayCommand] private void ToggleMute() { IsMuted = !IsMuted; }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        _remoteFullscreenWindow?.Close();
        if (_session is not null)
        {
            await _session.DisconnectAsync();
        }
        StopSessionCleanup();
        if (SessionTeardownRequested is not null)
            await SessionTeardownRequested.Invoke();
    }

    [RelayCommand]
    private async Task StopSessionAsync()
    {
        if (_session is not null)
        {
            await _session.DisconnectAsync();
        }
        StopSessionCleanup();
    }

    [RelayCommand]
    private void SendChatMessage()
    {
        if (string.IsNullOrWhiteSpace(ChatInput)) return;

        ChatMessages.Add(new ChatMessageItem
        {
            SenderName = "You",
            Content = ChatInput,
            Timestamp = DateTime.Now.ToString("HH:mm"),
            IsOwnMessage = true
        });

        ChatInput = string.Empty;
    }

    [RelayCommand]
    private void UploadFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = _localization.GetString("Session_Upload"),
            Filter = "All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true && _session is not null)
        {
            var item = new FileTransferItem
            {
                FileName = System.IO.Path.GetFileName(dialog.FileName),
                Progress = 0,
                Status = "Starting..."
            };
            ActiveTransfers.Add(item);
            _ = _session.SendFileAsync(dialog.FileName);
        }
    }

    public void ResetIdleUi() => StopSessionCleanup();

    public void StartSession(string sessionId, string customerDevice)
    {
        SessionId = sessionId;
        CustomerDevice = customerDevice;
        IsConnected = true;
        IsScreenSharing = true;
        _sessionStartUtc = DateTime.UtcNow;
        ConnectionStatus = _localization.GetString("Dashboard_Connected");
        ConnectionStatusColor = "#10B981";
        IsMouseControlEnabled = true;
        IsKeyboardControlEnabled = true;
        _session?.SetKeyboardCapture(true);
        TransportState = "Connecting";

        StartTimer();
        StartDiagnosticsTimer();
    }

    private void StopSessionCleanup()
    {
        IsConnected = false;
        IsScreenSharing = false;
        TransportState = "Disconnected";
        StopTimer();
        StopDiagnosticsTimer();
        ConnectionStatus = "Disconnected";
        ConnectionStatusColor = "#EF4444";
        RemoteScreenImage = null;
        Fps = 0;
        Bitrate = 0;
        Latency = 0;
        OnPropertyChanged(nameof(FpsLabel));
        OnPropertyChanged(nameof(BitrateLabel));
        OnPropertyChanged(nameof(LatencyLabel));
    }

    private void OnFrameRendered(object? sender, ImageSource frame)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            RemoteScreenImage = frame;
        });
    }

    private void OnSessionEnded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _session))
            return;

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!ReferenceEquals(sender, _session))
                return;
            StopSessionCleanup();
        });
    }

    private void OnLogMessage(object? sender, string message)
    {
        System.Diagnostics.Debug.WriteLine($"[Session] {message}");
    }

    private void OnFileTransferProgress(object? sender, FileTransferProgressEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var item = ActiveTransfers.FirstOrDefault(t => t.FileName == e.FileName);
            if (item is null)
            {
                item = new FileTransferItem { FileName = e.FileName };
                ActiveTransfers.Add(item);
            }
            item.Progress = e.Progress;
            item.Status = e.Status;
            HasActiveTransfers = ActiveTransfers.Count > 0;
        });
    }

    private void StartDiagnosticsTimer()
    {
        StopDiagnosticsTimer();
        _diagnosticsTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _diagnosticsTimer.Tick += OnDiagnosticsTick;
        _diagnosticsTimer.Start();
    }

    private void StopDiagnosticsTimer()
    {
        if (_diagnosticsTimer is not null)
        {
            _diagnosticsTimer.Stop();
            _diagnosticsTimer.Tick -= OnDiagnosticsTick;
            _diagnosticsTimer = null;
        }
    }

    private void OnDiagnosticsTick(object? sender, EventArgs e)
    {
        if (_session is null) return;

        var diag = _session.GetDiagnostics();
        TransportState = diag.IsConnected ? "Connected" : "Disconnected";
        DroppedFrames = diag.FramesReceived > 0 ? diag.FramesReceived - diag.FramesRendered : 0;
        Fps = diag.Fps;
        Bitrate = diag.BitrateBps;
        Latency = diag.LatencyMs;
        OnPropertyChanged(nameof(FpsLabel));
        OnPropertyChanged(nameof(BitrateLabel));
        OnPropertyChanged(nameof(LatencyLabel));

        if (diag.LastFrameUtc.HasValue)
        {
            var age = (DateTime.UtcNow - diag.LastFrameUtc.Value).TotalSeconds;
            if (age > 5)
                TransportState = "Stalled";
        }
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
        if (IsConnected)
        {
            var elapsed = DateTime.UtcNow - _sessionStartUtc;
            SessionDuration = elapsed.ToString(@"hh\:mm\:ss");
        }
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(SessionTimerLabel));
        OnPropertyChanged(nameof(FpsLabel));
        OnPropertyChanged(nameof(BitrateLabel));
        OnPropertyChanged(nameof(LatencyLabel));
        OnPropertyChanged(nameof(IdleTitle));
        OnPropertyChanged(nameof(IdleHint));
        OnPropertyChanged(nameof(ConnectingScreen));
        OnPropertyChanged(nameof(MouseLabel));
        OnPropertyChanged(nameof(KeyboardLabel));
        OnPropertyChanged(nameof(ClipboardLabel));
        OnPropertyChanged(nameof(RemoteCursorLabel));
        OnPropertyChanged(nameof(UploadLabel));
        OnPropertyChanged(nameof(FullscreenLabel));
        OnPropertyChanged(nameof(DisconnectLabel));
        OnPropertyChanged(nameof(TransfersLabel));
        OnPropertyChanged(nameof(StreamFpsLabel));
        OnPropertyChanged(nameof(StreamQualityLabel));
        OnPropertyChanged(nameof(StreamResolutionLabel));
    }

    public string SessionTimerLabel => string.Format(_localization.GetString("Session_Timer"), SessionDuration);
    public string FpsLabel => string.Format(_localization.GetString("Session_FPS"), Fps.ToString("F1"));
    public string BitrateLabel => string.Format(_localization.GetString("Session_Bitrate"), (Bitrate / 1000).ToString("F0"));
    public string LatencyLabel => string.Format(_localization.GetString("Session_Latency"), Latency.ToString("F0"));
    public string IdleTitle => _localization.GetString("Session_IdleTitle");
    public string IdleHint => _localization.GetString("Session_IdleHint");
    public string ConnectingScreen => _localization.GetString("Session_ConnectingScreen");
    public string MouseLabel => _localization.GetString("Session_MouseControl");
    public string KeyboardLabel => _localization.GetString("Session_KeyboardControl");
    public string ClipboardLabel => _localization.GetString("Session_Clipboard");
    public string RemoteCursorLabel => _localization.GetString("Session_ShowRemoteCursor");
    public string UploadLabel => _localization.GetString("Session_Upload");
    public string FullscreenLabel => _localization.GetString("Session_Fullscreen");
    public string DisconnectLabel => _localization.GetString("Session_Disconnect");
    public string TransfersLabel => _localization.GetString("Session_FileTransfer");
    public string StreamFpsLabel => _localization.GetString("Session_StreamFps");
    public string StreamQualityLabel => _localization.GetString("Session_StreamQuality");
    public string StreamResolutionLabel => _localization.GetString("Session_StreamResolution");

    public async ValueTask DisposeAsync()
    {
        StopSessionCleanup();
        if (_session is not null)
        {
            _session.FrameRendered -= OnFrameRendered;
            _session.SessionEnded -= OnSessionEnded;
            _session.LogMessage -= OnLogMessage;
            _session.FileTransferProgress -= OnFileTransferProgress;
            await _session.DisposeAsync();
        }
    }
}

public class ChatMessageItem
{
    public string SenderName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public bool IsOwnMessage { get; set; }
}

public partial class FileTransferItem : ObservableObject
{
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = string.Empty;
}
