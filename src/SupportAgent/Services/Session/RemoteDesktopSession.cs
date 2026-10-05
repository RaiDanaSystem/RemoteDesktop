using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using RemoteSupport.Shared.Clipboard;
using RemoteSupport.Shared.Diagnostics;
using RemoteSupport.Shared.RemoteInput;
using RemoteSupport.Shared.ScreenStreaming;
using RemoteSupport.Shared.ScreenStreaming.Encoding;
using RemoteSupport.Shared.Transport;
using RemoteSupport.Shared.Transport.Messages;
using RemoteSupport.Shared.Transport.Direct;
using RemoteSupport.Shared.Transport.WebRtc;
using RemoteSupport.Shared.FileTransfer;
using SupportAgent.Services.Input;
using SupportAgent.Services.Screen;

namespace SupportAgent.Services.Session;

/// <summary>
/// Orchestrates the remote desktop session on the SupportAgent side.
/// Initiates WebRTC as offerer, receives screen frames for rendering,
/// captures local mouse input for remote injection, and handles clipboard sync.
/// </summary>
public sealed class RemoteDesktopSession : IAsyncDisposable
{
    private readonly WebRtcSessionManager _sessionManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RemoteDesktopSession> _logger;
    private readonly ClipboardManager _clipboardManager;
    private readonly CoordinateMapper _coordinateMapper = new();
    private readonly WpfMouseCapture _mouseCapture;

    private WpfFrameRenderer? _frameRenderer;
    private CancellationTokenSource? _sessionCts;
    private Guid _sessionId;
    private bool _disposed;
    private bool _clipboardEchoBlock;
    private UIElement? _captureElement;
    private bool _mouseCaptureEnabled = true;
    private readonly WinFormsClipboardMonitor _osClipboard = new();
    private FileTransferPump? _filePump;
    private bool _keyboardEnabled = true;
    private bool _clipboardSyncEnabled = true;
    private int _streamFps = 20;
    private int _streamQuality = 55;
    private int _streamMaxWidth; // 0 = automatic

    public event EventHandler<FileTransferProgressEventArgs>? FileTransferProgress;

    // Diagnostics
    private int _framesReceived;
    private int _framesRendered;
    private int _inputEventsSent;
    private int _inputSendFailures;
    private int _clipboardMessagesSent;
    private int _clipboardMessagesReceived;
    private DateTime? _lastFrameUtc;
    private int _remoteWidth;
    private int _remoteHeight;
    private long _bytesReceived;
    private int _diagFramesSnapshot;
    private long _diagBytesSnapshot;
    private long _diagTickMs;
    private double _measuredFps;
    private long _measuredBitrateBps;
    private double _measuredLatencyMs;

    public bool IsConnected => _sessionManager.IsConnected;
    public bool IsMouseCaptureActive => _mouseCapture.IsCapturing;
    public CoordinateMapper CoordinateMapper => _coordinateMapper;

    public event EventHandler? SessionStarted;
    public event EventHandler? SessionEnded;
    public event EventHandler<FrameData>? FrameReceived;
    public event EventHandler<ImageSource>? FrameRendered;
    public event EventHandler<string>? LogMessage;
    public event EventHandler<SupportSessionDiagnostics>? DiagnosticsUpdated;

    public RemoteDesktopSession(
        WebRtcSessionManager sessionManager,
        ILoggerFactory loggerFactory,
        ClipboardManager clipboardManager)
    {
        _sessionManager = sessionManager;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RemoteDesktopSession>();
        _clipboardManager = clipboardManager;
        _mouseCapture = new WpfMouseCapture(_coordinateMapper);

        _sessionManager.TransportMessageReceived += OnTransportMessageReceived;
        _sessionManager.Connected += OnConnected;
        _sessionManager.Disconnected += OnDisconnected;
        _sessionManager.Error += OnError;

        _clipboardManager.TextReceived += OnOsClipboardText;
        _osClipboard.LocalTextChanged += OnOsClipboardText;
        _osClipboard.LocalFilesChanged += OnOsClipboardFiles;
        _filePump = new FileTransferPump((type, payload, ct) => _sessionManager.SendAsync(type, payload, ct));
        _filePump.Progress += (name, pct, status, isClipboard) =>
        {
            if (isClipboard) return;
            FileTransferProgress?.Invoke(this, new FileTransferProgressEventArgs(name, pct, status));
        };
        _filePump.OfferNeedsConsent += offer => _ = _filePump.RespondToOfferAsync(offer, accept: true);
        _filePump.ClipboardBatchReady += (_, paths) => _osClipboard.SetRemoteFiles(paths);
        _filePump.Log += Log;
    }

    /// <summary>
    /// Connects the WebRTC signaling channel to the server.
    /// Must be called before starting a session.
    /// </summary>
    public async Task ConnectSignalingAsync(string serverUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        await _sessionManager.ConnectSignalingAsync(serverUrl, accessToken, cancellationToken);
        Log("Signaling connected");
    }

    /// <summary>
    /// Initiates a remote desktop session with the CustomerAgent.
    /// </summary>
    public Task StartSessionAsync(Guid sessionId, string customerDevice,
        Action<BitmapSource> onFrameRendered, UIElement? captureElement = null,
        CancellationToken cancellationToken = default)
        => StartCoreAsync(sessionId, onFrameRendered, captureElement,
            () => _sessionManager.ConnectAsOffererAsync(sessionId, cancellationToken),
            "Initiated WebRTC connection as offerer");

    /// <summary>
    /// Starts a session over a server-less LAN TCP channel that was accepted by the remote PC.
    /// </summary>
    public Task StartDirectSessionAsync(Guid sessionId, TcpDataChannel channel,
        Action<BitmapSource> onFrameRendered, UIElement? captureElement = null)
        => StartCoreAsync(sessionId, onFrameRendered, captureElement,
            () => _sessionManager.AttachDirectChannelAsync(channel, isInitiator: true),
            "Started direct LAN session");

    private async Task StartCoreAsync(Guid sessionId,
        Action<BitmapSource> onFrameRendered, UIElement? captureElement,
        Func<Task> connectAsync, string startedMessage)
    {
        _sessionId = sessionId;
        _sessionCts = new CancellationTokenSource();
        _captureElement = captureElement;
        _framesReceived = 0;
        _framesRendered = 0;
        _inputEventsSent = 0;
        _clipboardMessagesSent = 0;
        _clipboardMessagesReceived = 0;
        _bytesReceived = 0;
        _diagFramesSnapshot = 0;
        _diagBytesSnapshot = 0;
        _diagTickMs = 0;
        _measuredFps = 0;
        _measuredBitrateBps = 0;
        _measuredLatencyMs = 0;

        _frameRenderer = new WpfFrameRenderer(
            source =>
            {
                Interlocked.Increment(ref _framesRendered);
                FrameRendered?.Invoke(this, source);
                onFrameRendered(source);
            },
            new JpegFrameEncoder());

        _clipboardManager.GrantConsent(sessionId.ToString());
        _osClipboard.Start();
        _mouseCaptureEnabled = true;

        if (captureElement is not null)
        {
            SetCaptureTarget(captureElement, restartMouse: true);
        }

        await connectAsync();
        Log(startedMessage);
        _ = RunPingLoopAsync(_sessionCts.Token);

        SessionStarted?.Invoke(this, EventArgs.Empty);
    }

    public void SetCaptureTarget(UIElement? element, bool restartMouse = true)
    {
        void Apply()
        {
            if (_captureElement is not null && !ReferenceEquals(_captureElement, element))
                _mouseCapture.StopCapture(_captureElement);

            _captureElement = element;
            if (restartMouse && _mouseCaptureEnabled && element is not null)
            {
                _mouseCapture.InputCaptured -= OnInputCaptured;
                _mouseCapture.InputCaptured += OnInputCaptured;
                _mouseCapture.StartCapture(element);
            }
        }

        if (element is not null && !element.Dispatcher.CheckAccess())
            element.Dispatcher.Invoke(Apply);
        else
            Apply();
    }

    /// <summary>
    /// Enables or disables mouse capture for remote input.
    /// </summary>
    public void SetMouseCapture(bool enabled)
    {
        _mouseCaptureEnabled = enabled;
        if (enabled && _captureElement is not null)
        {
            _mouseCapture.InputCaptured -= OnInputCaptured;
            _mouseCapture.InputCaptured += OnInputCaptured;
            _mouseCapture.StartCapture(_captureElement);
        }
        else
        {
            if (_captureElement is not null)
                _mouseCapture.StopCapture(_captureElement);
            _mouseCapture.InputCaptured -= OnInputCaptured;
        }
    }

    public async Task SetShowRemoteCursorAsync(bool enabled)
    {
        _mouseCapture.SendPointerMoves = enabled;
        if (!IsConnected) return;
        try
        {
            var control = new ControlMessage
            {
                Action = ControlAction.ShowRemoteCursor,
                Metadata = new Dictionary<string, string> { ["Enabled"] = enabled ? "true" : "false" }
            };
            await _sessionManager.SendAsync(TransportMessageType.Control, control.Serialize());
        }
        catch { }
    }

    /// <param name="maxWidth">0 = automatic (derived from quality); otherwise 640..3840 (e.g. 3840 for 4K).</param>
    public async Task SendStreamSettingsAsync(int fps, int quality, int? maxWidth = null)
    {
        _streamFps = Math.Clamp(fps, 5, 60);
        _streamQuality = Math.Clamp(quality, 20, 80);
        if (maxWidth.HasValue)
            _streamMaxWidth = maxWidth.Value <= 0 ? 0 : Math.Clamp(maxWidth.Value, 640, 3840);
        if (!IsConnected) return;
        try
        {
            var control = new ControlMessage
            {
                Action = ControlAction.StreamSettings,
                Fps = _streamFps,
                Quality = _streamQuality,
                Metadata = new Dictionary<string, string>
                {
                    ["Fps"] = _streamFps.ToString(),
                    ["fps"] = _streamFps.ToString(),
                    ["Quality"] = _streamQuality.ToString(),
                    ["quality"] = _streamQuality.ToString(),
                    ["MaxWidth"] = _streamMaxWidth.ToString(),
                    ["maxWidth"] = _streamMaxWidth.ToString()
                }
            };
            await _sessionManager.SendAsync(TransportMessageType.Control, control.Serialize());
            Log($"Requested stream fps={_streamFps} quality={_streamQuality} maxWidth={_streamMaxWidth}");
        }
        catch { }
    }

    public void SetKeyboardCapture(bool enabled)
    {
        _keyboardEnabled = enabled;
        _mouseCapture.KeyboardEnabled = enabled;
    }

    public void SetClipboardSync(bool enabled)
    {
        _clipboardSyncEnabled = enabled;
        if (enabled) _osClipboard.Start();
        else _osClipboard.Stop();
    }

    public Task SendFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _filePump is null || !File.Exists(filePath))
            return Task.CompletedTask;
        return _filePump.SendFileAsync(filePath, placeOnClipboard: false, Guid.NewGuid(), 1, cancellationToken);
    }
    public async Task SendClipboardTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return;

        try
        {
            var transport = new ClipboardTransport
            {
                Type = ClipboardMessageType.TextSync,
                Text = text,
                SourceId = _sessionId.ToString(),
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            await _sessionManager.SendAsync(TransportMessageType.Clipboard, transport.Serialize(), cancellationToken);
            Interlocked.Increment(ref _clipboardMessagesSent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send clipboard text");
        }
    }

    /// <summary>
    /// Sends a disconnect signal to the CustomerAgent and cleans up.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (IsConnected)
        {
            try
            {
                var control = new ControlMessage
                {
                    Action = ControlAction.Disconnect,
                    Reason = "Support agent disconnected"
                };
                await _sessionManager.SendAsync(TransportMessageType.Control, control.Serialize());
            }
            catch { }
        }

        await CleanupAsync();
    }

    public SupportSessionDiagnostics GetDiagnostics()
    {
        var now = Environment.TickCount64;
        if (_diagTickMs == 0)
            _diagTickMs = now;
        var elapsedSec = Math.Max((now - _diagTickMs) / 1000.0, 0.25);
        var frames = _framesReceived - _diagFramesSnapshot;
        var bytes = _bytesReceived - _diagBytesSnapshot;
        _diagFramesSnapshot = _framesReceived;
        _diagBytesSnapshot = _bytesReceived;
        _diagTickMs = now;
        _measuredFps = frames / elapsedSec;
        _measuredBitrateBps = (long)(bytes * 8 / elapsedSec);

        return new SupportSessionDiagnostics
        {
            IsConnected = IsConnected,
            IsMouseCaptureActive = IsMouseCaptureActive,
            FramesReceived = _framesReceived,
            FramesRendered = _framesRendered,
            InputEventsSent = _inputEventsSent,
            InputSendFailures = _inputSendFailures,
            ClipboardMessagesSent = _clipboardMessagesSent,
            ClipboardMessagesReceived = _clipboardMessagesReceived,
            LastFrameUtc = _lastFrameUtc,
            RemoteWidth = _remoteWidth,
            RemoteHeight = _remoteHeight,
            Fps = _measuredFps,
            BitrateBps = _measuredBitrateBps,
            LatencyMs = _measuredLatencyMs
        };
    }

    private void OnConnected(object? sender, EventArgs e)
    {
        Log("WebRTC data channel connected");
        _ = SendStreamSettingsAsync(_streamFps, _streamQuality);
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        Log("WebRTC disconnected");
        _ = CleanupAsync();
    }

    private void OnError(object? sender, Exception ex)
    {
        Log($"WebRTC error: {ex.Message}");
    }

    private void OnTransportMessageReceived(object? sender, TransportEnvelope envelope)
    {
        if (_disposed) return;

        switch (envelope.MessageType)
        {
            case TransportMessageType.ScreenFrame:
                HandleScreenFrame(envelope.Payload);
                break;

            case TransportMessageType.Clipboard:
                HandleClipboardMessage(envelope.Payload);
                break;

            case TransportMessageType.Control:
                HandleControlMessage(envelope.Payload);
                break;

            case TransportMessageType.Pong:
                HandlePong(envelope.Payload);
                break;

            case TransportMessageType.FileOffer:
            case TransportMessageType.FileChunk:
            case TransportMessageType.FileAck:
                HandleFileMessage(envelope.Payload);
                break;
        }
    }

    private void HandleScreenFrame(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0 || _frameRenderer is null) return;

        Interlocked.Increment(ref _framesReceived);
        Interlocked.Add(ref _bytesReceived, payload.Length);
        _lastFrameUtc = DateTime.UtcNow;

        var screenFrame = ScreenFrameTransport.Deserialize(payload.ToArray());
        if (screenFrame is null)
        {
            if (_framesReceived <= 5)
                Log($"Screen frame deserialize failed ({payload.Length} bytes)");
            return;
        }

        if (_framesReceived <= 5 || _framesReceived % 40 == 0)
        {
            SessionTrace.Write("support-video",
                $"recv #{_framesReceived} {screenFrame.Format} {screenFrame.Width}x{screenFrame.Height} payload={screenFrame.FramePayload.Length}");
        }

        _remoteWidth = screenFrame.Width;
        _remoteHeight = screenFrame.Height;

        void UpdateMapper(int displayW, int displayH)
        {
            if (displayW < 32 || displayH < 32)
                return;
            if (Math.Abs(_coordinateMapper.DisplayWidth - displayW) < 3
                && Math.Abs(_coordinateMapper.DisplayHeight - displayH) < 3
                && _coordinateMapper.RemoteWidth == _remoteWidth
                && _coordinateMapper.RemoteHeight == _remoteHeight)
                return;
            _coordinateMapper.UpdateDimensions(_remoteWidth, _remoteHeight, displayW, displayH);
            SessionTrace.Write("support-input",
                $"mapper remote={_remoteWidth}x{_remoteHeight} display={displayW}x{displayH}");
        }

        if (_captureElement is not null)
        {
            _captureElement.Dispatcher.BeginInvoke(() =>
            {
                var displayW = (int)Math.Max(1, _captureElement.RenderSize.Width);
                var displayH = (int)Math.Max(1, _captureElement.RenderSize.Height);
                UpdateMapper(displayW, displayH);
            });
        }
        else
        {
            UpdateMapper(_remoteWidth, _remoteHeight);
        }

        var frameData = screenFrame.ToFrameData();
        FrameReceived?.Invoke(this, frameData);

        _ = _frameRenderer.RenderFrameAsync(frameData.FrameBytes);
    }

    private void OnInputCaptured(InputEvent inputEvent)
    {
        if (!IsConnected || _disposed) return;
        if (inputEvent.Type is InputEventType.KeyboardKey or InputEventType.KeyboardChar && !_keyboardEnabled)
            return;

        SessionTrace.Mouse("support-input",
            $"{inputEvent.Type} stream=({inputEvent.MouseX},{inputEvent.MouseY}) vk={inputEvent.VirtualKeyCode}");
        _ = SendInputEventAsync(inputEvent);
    }

    private async Task SendInputEventAsync(InputEvent inputEvent)
    {
        try
        {
            var transport = InputTransport.FromInputEvent(inputEvent);
            var payload = transport.Serialize();

            await _sessionManager.SendAsync(TransportMessageType.Input, payload);
            Interlocked.Increment(ref _inputEventsSent);
        }
        catch
        {
            Interlocked.Increment(ref _inputSendFailures);
        }
    }

    private void HandleClipboardMessage(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0) return;

        var transport = ClipboardTransport.Deserialize(payload.ToArray());
        if (transport is null) return;

        // Prevent echo loop
        if (transport.SourceId == _sessionId.ToString())
            return;

        Interlocked.Increment(ref _clipboardMessagesReceived);

        _clipboardEchoBlock = true;
        try
        {
            var msg = transport.ToClipboardMessage();
            _clipboardManager.HandleReceivedMessage(msg);
            if (!string.IsNullOrEmpty(msg.Text) && _clipboardSyncEnabled)
                _osClipboard.SetRemoteText(msg.Text);
        }
        finally
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                _clipboardEchoBlock = false;
            });
        }
    }

    private void HandleControlMessage(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0) return;

        var control = ControlMessage.Deserialize(payload.ToArray());
        if (control is null) return;

        switch (control.Action)
        {
            case ControlAction.Disconnect:
                Log("Remote disconnect received");
                _ = CleanupAsync();
                break;

            case ControlAction.ScreenResolutionChanged:
                SessionTrace.Write("support-input", "ignored ScreenResolutionChanged (using stream size)");
                break;
        }
    }

    private void HandlePong(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0) return;

        try
        {
            var pong = JsonSerializer.Deserialize<PongMessage>(payload.Span);
            if (pong is not null)
            {
                var rtt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pong.TimestampMs;
                if (rtt >= 0 && rtt < 60_000)
                    _measuredLatencyMs = rtt;
            }
        }
        catch { }
    }

    private void OnOsClipboardText(string text)
    {
        if (_clipboardEchoBlock || !_clipboardSyncEnabled || _disposed || !IsConnected) return;
        _ = SendClipboardTextAsync(text);
    }

    private void OnOsClipboardFiles(string[] paths)
    {
        if (_clipboardEchoBlock || !_clipboardSyncEnabled || _disposed || !IsConnected || _filePump is null)
            return;
        var batchId = Guid.NewGuid();
        var copy = paths.ToArray();
        _ = Task.Run(async () =>
        {
            foreach (var path in copy)
                await _filePump.SendFileAsync(path, placeOnClipboard: true, batchId, copy.Length);
        });
    }

    private void HandleFileMessage(ReadOnlyMemory<byte> payload)
    {
        var msg = FileTransferMessage.Deserialize(payload.ToArray());
        if (msg is null || _filePump is null) return;
        _filePump.HandleMessage(msg, fileTransferEnabled: true, clipboardEnabled: _clipboardSyncEnabled);
    }

    private async Task CleanupAsync()
    {
        if (_disposed) return;

        try
        {
            if (_captureElement is not null)
            {
                _mouseCapture.StopCapture(_captureElement);
                _mouseCapture.InputCaptured -= OnInputCaptured;
            }

            _sessionCts?.Cancel();

            _clipboardManager.RevokeConsent();
            _clipboardManager.TextReceived -= OnOsClipboardText;
            _osClipboard.LocalTextChanged -= OnOsClipboardText;
            _osClipboard.LocalFilesChanged -= OnOsClipboardFiles;
            _osClipboard.Stop();

            if (_frameRenderer is not null)
            {
                await _frameRenderer.ClearAsync();
                _frameRenderer = null;
            }

            await _sessionManager.ResetPeerAsync();

            SessionEnded?.Invoke(this, EventArgs.Empty);
            Log("Session cleaned up");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during session cleanup");
        }
    }

    private async Task RunPingLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                await Task.Delay(2000, cancellationToken);
                if (IsConnected)
                    await _sessionManager.SendPingAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ping loop ended");
        }
    }

    private void Log(string message)
    {
        _logger.LogInformation("{Message}", message);
        LogMessage?.Invoke(this, message);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await CleanupAsync();
        _disposed = true;

        _sessionManager.TransportMessageReceived -= OnTransportMessageReceived;
        _sessionManager.Connected -= OnConnected;
        _sessionManager.Disconnected -= OnDisconnected;
        _sessionManager.Error -= OnError;
        _clipboardManager.TextReceived -= OnOsClipboardText;
        _osClipboard.LocalTextChanged -= OnOsClipboardText;
        _osClipboard.LocalFilesChanged -= OnOsClipboardFiles;
        _osClipboard.Dispose();
        _filePump?.Dispose();
        _sessionCts?.Dispose();
    }
}

public sealed class SupportSessionDiagnostics
{
    public bool IsConnected { get; init; }
    public bool IsMouseCaptureActive { get; init; }
    public int FramesReceived { get; init; }
    public int FramesRendered { get; init; }
    public int InputEventsSent { get; init; }
    public int InputSendFailures { get; init; }
    public int ClipboardMessagesSent { get; init; }
    public int ClipboardMessagesReceived { get; init; }
    public DateTime? LastFrameUtc { get; init; }
    public int RemoteWidth { get; init; }
    public int RemoteHeight { get; init; }
    public double Fps { get; init; }
    public long BitrateBps { get; init; }
    public double LatencyMs { get; init; }
}

public sealed class FileTransferProgressEventArgs : EventArgs
{
    public FileTransferProgressEventArgs(string fileName, double progress, string status)
    {
        FileName = fileName;
        Progress = progress;
        Status = status;
    }

    public string FileName { get; }
    public double Progress { get; }
    public string Status { get; }
}
