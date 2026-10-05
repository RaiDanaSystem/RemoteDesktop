using System.IO;
using System.Text.Json;
using CustomerAgent.Services.Input;
using CustomerAgent.Services.Interfaces;
using Microsoft.Extensions.Logging;
using RemoteSupport.Shared.Clipboard;
using RemoteSupport.Shared.FileTransfer;
using RemoteSupport.Shared.Diagnostics;
using RemoteSupport.Shared.RemoteInput;
using RemoteSupport.Shared.ScreenStreaming;
using RemoteSupport.Shared.ScreenStreaming.Capture;
using RemoteSupport.Shared.ScreenStreaming.Encoding;
using RemoteSupport.Shared.Transport;
using RemoteSupport.Shared.Transport.Messages;
using RemoteSupport.Shared.Transport.WebRtc;

namespace CustomerAgent.Services.Session;

/// <summary>
/// Orchestrates the remote desktop session on the CustomerAgent side.
/// Handles WebRTC answerer lifecycle, screen streaming over DataChannel,
/// remote input reception with consent enforcement, and clipboard sync.
/// </summary>
public sealed class RemoteDesktopSession : IAsyncDisposable
{
    private readonly WebRtcSessionManager _sessionManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RemoteDesktopSession> _logger;
    private readonly InputConsentManager _consentManager;
    private readonly ClipboardManager _clipboardManager;
    private readonly WindowsInputInjection _inputInjection;
    private readonly ICustomerApiClient _apiClient;

    private ScreenStreamingManager? _screenStreaming;
    private GdiScreenCapture? _gdiCapture;
    private CancellationTokenSource? _sessionCts;
    private Guid _sessionId;
    private bool _disposed;
    private bool _clipboardEchoBlock;
    private DateTime _lastClipboardSendUtc;
    private readonly WinFormsClipboardMonitor _osClipboard = new();
    private FileTransferPump? _filePump;
    private bool _clipboardSyncEnabled = true;
    private bool _fileTransferEnabled = true;
    private FileTransferMessage? _pendingOffer;
    private int _streamFps = 20;
    private int _streamQuality = 55;

    public event EventHandler<IncomingFileOfferEventArgs>? IncomingFileOffered;

    // Diagnostics
    private int _framesSent;
    private int _framesDropped;
    private int _inputEventsReceived;
    private int _inputEventsInjected;
    private int _inputEventsRejected;

    public bool IsConnected => _sessionManager.IsConnected;
    public bool IsStreaming => _screenStreaming?.IsStreaming == true;
    public bool IsConsented => _consentManager.IsConsented;

    public event EventHandler? SessionStarted;
    public event EventHandler? SessionEnded;
    public event EventHandler<string>? LogMessage;
    public event EventHandler<SessionDiagnostics>? DiagnosticsUpdated;

    public RemoteDesktopSession(
        WebRtcSessionManager sessionManager,
        ILoggerFactory loggerFactory,
        InputConsentManager consentManager,
        ClipboardManager clipboardManager,
        WindowsInputInjection inputInjection,
        ICustomerApiClient apiClient)
    {
        _sessionManager = sessionManager;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RemoteDesktopSession>();
        _consentManager = consentManager;
        _clipboardManager = clipboardManager;
        _inputInjection = inputInjection;
        _apiClient = apiClient;

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
            Log($"{name}: {status} {pct:0}%");
        };
        _filePump.OfferNeedsConsent += offer =>
        {
            _pendingOffer = offer;
            IncomingFileOffered?.Invoke(this, new IncomingFileOfferEventArgs(offer.FileName ?? "file", offer.FileSize));
        };
        _filePump.ClipboardBatchReady += (_, paths) => _osClipboard.SetRemoteFiles(paths);
        _filePump.Log += Log;
    }

    /// <summary>
    /// Connects the WebRTC signaling channel to the server.
    /// Must be called before session preparation.
    /// </summary>
    public async Task ConnectSignalingAsync(string serverUrl, string deviceIdentifier, CancellationToken cancellationToken = default)
    {
        var token = await _apiClient.GetDeviceTokenAsync(deviceIdentifier, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("Failed to obtain device token for signaling");
        }

        await _sessionManager.ConnectSignalingAsync(serverUrl, token, cancellationToken);
        Log("Signaling connected");
    }

    /// <summary>
    /// Prepares the session to receive a WebRTC offer from the SupportAgent.
    /// Called after the customer accepts a connection request.
    /// </summary>
    public async Task PrepareSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        _sessionId = sessionId;
        _sessionCts?.Cancel();
        _sessionCts = new CancellationTokenSource();
        _framesSent = 0;
        _framesDropped = 0;
        _inputEventsReceived = 0;
        _inputEventsInjected = 0;
        _inputEventsRejected = 0;

        if (_screenStreaming is not null)
        {
            await _screenStreaming.StopStreamingAsync(cancellationToken);
            await _screenStreaming.DisposeAsync();
            _screenStreaming = null;
            _gdiCapture = null;
        }

        await _sessionManager.PrepareAsAnswererAsync(sessionId, cancellationToken);

        Log("Session prepared, waiting for offer from support agent");
        SessionStarted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Grants or revokes input consent for the current session.
    /// </summary>
    public void SetInputConsent(bool granted)
    {
        if (granted)
            _consentManager.GrantConsent(_sessionId.ToString());
        else
            _consentManager.RevokeConsent();

        _inputInjection.IsEnabled = granted;
        Log($"Input consent {(granted ? "granted" : "revoked")}");
    }

    public void SetClipboardConsent(bool granted)
    {
        _clipboardSyncEnabled = granted;
        if (granted)
        {
            _clipboardManager.GrantConsent(_sessionId.ToString());
            _osClipboard.Start();
        }
        else
        {
            _clipboardManager.RevokeConsent();
            _osClipboard.Stop();
        }
    }

    public void SetFileTransferConsent(bool granted) => _fileTransferEnabled = granted;

    public async Task RespondToFileOfferAsync(bool accept)
    {
        if (_pendingOffer is null || _filePump is null) return;
        var offer = _pendingOffer;
        _pendingOffer = null;
        await _filePump.RespondToOfferAsync(offer, accept);
    }

    public SessionDiagnostics GetDiagnostics()
    {
        return new SessionDiagnostics
        {
            IsConnected = IsConnected,
            IsStreaming = IsStreaming,
            IsConsented = IsConsented,
            FramesSent = _framesSent,
            FramesDropped = _framesDropped,
            InputEventsReceived = _inputEventsReceived,
            InputEventsInjected = _inputEventsInjected,
            InputEventsRejected = _inputEventsRejected
        };
    }

    private void OnConnected(object? sender, EventArgs e)
    {
        Log("WebRTC data channel connected");
        _ = StartScreenStreamingAsync();
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

    private async Task StartScreenStreamingAsync()
    {
        if (_disposed) return;

        try
        {
            if (_screenStreaming is not null)
            {
                await _screenStreaming.StopStreamingAsync();
                await _screenStreaming.DisposeAsync();
                _screenStreaming = null;
                _gdiCapture = null;
            }

            var tileOptions = new RemoteSupport.Shared.ScreenStreaming.Tiling.TileStreamingOptions
            {
                MaxWidth = MaxWidthForQuality(_streamQuality),
                JpegQuality = _streamQuality,
                TileSize = 96,
                KeyframeIntervalFrames = Math.Max(_streamFps, 15)
            };
            var capture = new GdiScreenCapture(_loggerFactory.CreateLogger<GdiScreenCapture>(), tileOptions.MaxWidth)
            {
                IncludeHardwareCursor = false
            };
            _gdiCapture = capture;
            var encoder = new JpegFrameEncoder(tileOptions.JpegQuality);

            _screenStreaming = new ScreenStreamingManager(
                capture, encoder,
                _loggerFactory.CreateLogger<ScreenStreamingManager>(),
                tileOptions);

            var result = await _screenStreaming.StartStreamingAsync(
                _sessionId.ToString(),
                _sessionManager.PeerId ?? "remote",
                targetFps: _streamFps,
                quality: _streamQuality,
                onFrameCaptured: SendFrameOverChannelAsync,
                cancellationToken: _sessionCts?.Token ?? CancellationToken.None);

            if (!result.IsSuccess)
            {
                Log($"Screen streaming failed to start: {result.ErrorMessage}");
            }
            else
            {
                Log("Screen streaming started (H.264, JPEG tiles fallback)");
                var control = new ControlMessage
                {
                    Action = ControlAction.ScreenResolutionChanged,
                    Metadata = new Dictionary<string, string>
                    {
                        ["Width"] = ((int)System.Windows.SystemParameters.PrimaryScreenWidth).ToString(),
                        ["Height"] = ((int)System.Windows.SystemParameters.PrimaryScreenHeight).ToString()
                    }
                };
                await _sessionManager.SendAsync(TransportMessageType.Control, control.Serialize());
            }
        }
        catch (Exception ex)
        {
            Log($"Error starting screen streaming: {ex.Message}");
        }
    }

    private async Task SendFrameOverChannelAsync(FrameData frame)
    {
        if (_disposed || !IsConnected) return;

        var sendBytes = 0;
        try
        {
            _inputInjection.SetFramebufferSize(
                frame.Width,
                frame.Height,
                frame.NativeWidth > 0 ? frame.NativeWidth : frame.Width,
                frame.NativeHeight > 0 ? frame.NativeHeight : frame.Height,
                frame.NativeOriginX,
                frame.NativeOriginY);

            var transport = ScreenFrameTransport.FromFrameData(frame);
            var payload = transport.Serialize();
            var envelope = new TransportEnvelope
            {
                MessageType = TransportMessageType.ScreenFrame,
                Payload = payload
            };

            var channel = _sessionManager.Channel;
            if (channel is null || channel.State != DataChannelState.Connected)
            {
                Interlocked.Increment(ref _framesDropped);
                return;
            }

            var bytes = envelope.Serialize();
            sendBytes = bytes.Length;
            SessionTrace.Stream("customer-video",
                $"send seq={frame.SequenceNumber} {frame.Width}x{frame.Height} envelope={sendBytes} tilesFormat={frame.Format}");

            await channel.SendAsync(bytes, _sessionCts?.Token ?? CancellationToken.None);
            Interlocked.Increment(ref _framesSent);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _framesDropped);
            if (_framesDropped <= 5)
                _logger.LogWarning(ex, "Failed to send screen frame ({Bytes} bytes)", sendBytes);
        }
    }

    private void OnTransportMessageReceived(object? sender, TransportEnvelope envelope)
    {
        if (_disposed) return;

        switch (envelope.MessageType)
        {
            case TransportMessageType.Input:
                HandleInputMessage(envelope.Payload);
                break;

            case TransportMessageType.Clipboard:
                HandleClipboardMessage(envelope.Payload);
                break;

            case TransportMessageType.Control:
                HandleControlMessage(envelope.Payload);
                break;

            case TransportMessageType.Ping:
                HandlePing(envelope.Payload);
                break;

            case TransportMessageType.FileOffer:
            case TransportMessageType.FileChunk:
            case TransportMessageType.FileAck:
                HandleFileMessage(envelope.Payload);
                break;
        }
    }

    private void HandleInputMessage(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0) return;

        Interlocked.Increment(ref _inputEventsReceived);

        var input = InputTransport.Deserialize(payload.ToArray());
        if (input is null) return;

        if (!_consentManager.IsConsented || !_consentManager.ValidateConsent(_sessionId.ToString()))
        {
            Interlocked.Increment(ref _inputEventsRejected);
            return;
        }

        SessionTrace.Mouse("customer-input",
            $"{input.Type} ({input.MouseX},{input.MouseY}) consented={_consentManager.IsConsented}");
        _ = InjectInputAsync(input);
    }

    private async Task InjectInputAsync(InputTransport input)
    {
        try
        {
            switch (input.Type)
            {
                case InputEventType.MouseMove:
                    await _inputInjection.InjectMouseEventAsync(input.MouseX, input.MouseY);
                    break;

                case InputEventType.MouseButton:
                    await _inputInjection.InjectMouseEventAsync(
                        input.MouseX, input.MouseY,
                        input.Button, input.Action);
                    break;

                case InputEventType.MouseWheel:
                    await _inputInjection.InjectMouseEventAsync(
                        input.MouseX, input.MouseY,
                        wheelDelta: input.WheelDelta);
                    break;

                case InputEventType.MouseDblClick:
                    await _inputInjection.InjectDoubleClickAsync(
                        input.MouseX, input.MouseY, input.Button);
                    break;

                case InputEventType.KeyboardKey:
                    await _inputInjection.InjectKeyboardEventAsync(
                        input.VirtualKeyCode, input.Action);
                    break;

                case InputEventType.KeyboardChar:
                    if (input.Character.HasValue)
                        await _inputInjection.InjectCharacterAsync(input.Character.Value);
                    break;
            }

            Interlocked.Increment(ref _inputEventsInjected);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject input event");
        }
    }

    private void HandleClipboardMessage(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0) return;

        var transport = ClipboardTransport.Deserialize(payload.ToArray());
        if (transport is null) return;

        // Prevent echo loop: ignore messages we sent ourselves
        if (transport.SourceId == _sessionId.ToString())
            return;

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
            // Unblock after a short delay to allow the clipboard write to complete
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
                Log("Remote disconnect requested");
                _ = CleanupAsync();
                break;

            case ControlAction.ShowRemoteCursor:
                var enabled = control.Metadata is not null
                    && control.Metadata.TryGetValue("Enabled", out var flag)
                    && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
                if (_gdiCapture is not null)
                    _gdiCapture.IncludeHardwareCursor = enabled;
                Log(enabled ? "Remote cursor overlay enabled" : "Remote cursor overlay disabled");
                break;

            case ControlAction.StreamSettings:
                ApplyStreamSettings(control);
                break;
        }
    }

    private void ApplyStreamSettings(ControlMessage control)
    {
        var fps = control.Fps;
        var quality = control.Quality;
        if (control.Metadata is not null)
        {
            if (fps is null)
            {
                foreach (var key in new[] { "Fps", "fps" })
                {
                    if (control.Metadata.TryGetValue(key, out var fpsText) && int.TryParse(fpsText, out var parsedFps))
                    {
                        fps = parsedFps;
                        break;
                    }
                }
            }
            if (quality is null)
            {
                foreach (var key in new[] { "Quality", "quality" })
                {
                    if (control.Metadata.TryGetValue(key, out var qualityText) && int.TryParse(qualityText, out var parsedQuality))
                    {
                        quality = parsedQuality;
                        break;
                    }
                }
            }
        }

        if (fps.HasValue)
            _streamFps = Math.Clamp(fps.Value, 5, 30);
        if (quality.HasValue)
            _streamQuality = Math.Clamp(quality.Value, 20, 80);

        var maxWidth = MaxWidthForQuality(_streamQuality);
        if (_gdiCapture is not null)
            _gdiCapture.MaxWidth = maxWidth;

        _screenStreaming?.ApplySettings(_streamFps, _streamQuality);
        Log($"Stream settings applied: fps={_streamFps} quality={_streamQuality} width={maxWidth}");
    }

    private static int MaxWidthForQuality(int quality) => quality switch
    {
        <= 30 => 960,
        <= 40 => 1120,
        <= 50 => 1280,
        <= 60 => 1440,
        <= 70 => 1600,
        _ => 1920
    };

    private void HandlePing(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0) return;

        try
        {
            var ping = JsonSerializer.Deserialize<PingMessage>(payload.Span);
            if (ping is not null)
            {
                var pong = new PongMessage
                {
                    PingId = ping.PingId,
                    TimestampMs = ping.TimestampMs
                };
                var pongPayload = JsonSerializer.SerializeToUtf8Bytes(pong);
                _ = _sessionManager.SendAsync(TransportMessageType.Pong, pongPayload);
            }
        }
        catch { }
    }

    private void OnOsClipboardText(string text)
    {
        if (_clipboardEchoBlock || !_clipboardSyncEnabled || _disposed || !IsConnected) return;
        if (!_clipboardManager.ValidateConsent(_sessionId.ToString())) return;
        _ = SendClipboardTextAsync(text);
    }

    private void OnOsClipboardFiles(string[] paths)
    {
        if (_clipboardEchoBlock || !_clipboardSyncEnabled || !_fileTransferEnabled || _disposed || !IsConnected)
            return;
        if (_filePump is null) return;
        if (!_clipboardManager.ValidateConsent(_sessionId.ToString())) return;
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
        _filePump.HandleMessage(msg, _fileTransferEnabled, _clipboardSyncEnabled);
    }

    private async Task SendClipboardTextAsync(string text)
    {
        try
        {
            var transport = new ClipboardTransport
            {
                Type = ClipboardMessageType.TextSync,
                Text = text,
                SourceId = _sessionId.ToString(),
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            var payload = transport.Serialize();

            await _sessionManager.SendAsync(TransportMessageType.Clipboard, payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send clipboard text");
        }
    }

    public async Task SendDisconnectAsync()
    {
        if (!IsConnected) return;

        try
        {
            var control = new ControlMessage
            {
                Action = ControlAction.Disconnect,
                Reason = "Customer disconnected"
            };

            await _sessionManager.SendAsync(TransportMessageType.Control, control.Serialize());
        }
        catch { }

        await CleanupAsync();
    }

    private async Task CleanupAsync()
    {
        if (_disposed) return;

        try
        {
            if (_screenStreaming is not null)
            {
                await _screenStreaming.StopStreamingAsync();
                await _screenStreaming.DisposeAsync();
                _screenStreaming = null;
                _gdiCapture = null;
            }

            _sessionCts?.Cancel();

            _consentManager.RevokeConsent();
            _clipboardManager.RevokeConsent();
            _inputInjection.IsEnabled = false;

            _clipboardManager.TextReceived -= OnOsClipboardText;
            _osClipboard.LocalTextChanged -= OnOsClipboardText;
            _osClipboard.LocalFilesChanged -= OnOsClipboardFiles;
            _osClipboard.Stop();

            SessionEnded?.Invoke(this, EventArgs.Empty);
            Log("Session cleaned up");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during session cleanup");
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

public sealed class SessionDiagnostics
{
    public bool IsConnected { get; init; }
    public bool IsStreaming { get; init; }
    public bool IsConsented { get; init; }
    public int FramesSent { get; init; }
    public int FramesDropped { get; init; }
    public int InputEventsReceived { get; init; }
    public int InputEventsInjected { get; init; }
    public int InputEventsRejected { get; init; }
}

public sealed class IncomingFileOfferEventArgs : EventArgs
{
    public IncomingFileOfferEventArgs(string fileName, long fileSize)
    {
        FileName = fileName;
        FileSize = fileSize;
    }

    public string FileName { get; }
    public long FileSize { get; }
}
