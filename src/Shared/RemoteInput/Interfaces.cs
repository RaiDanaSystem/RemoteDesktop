namespace RemoteSupport.Shared.RemoteInput;

public interface IInputInjection : IAsyncDisposable
{
    Task<bool> InjectMouseEventAsync(int x, int y, MouseButton? button = null, KeyAction? action = null, int wheelDelta = 0, CancellationToken cancellationToken = default);
    Task<bool> InjectKeyboardEventAsync(ushort virtualKeyCode, KeyAction action, CancellationToken cancellationToken = default);
    Task<bool> InjectCharacterAsync(char character, CancellationToken cancellationToken = default);
    Task<bool> InjectDoubleClickAsync(int x, int y, MouseButton button, CancellationToken cancellationToken = default);
    bool IsEnabled { get; set; }
}

public interface IInputCapture : IAsyncDisposable
{
    Task StartCaptureAsync(CancellationToken cancellationToken = default);
    Task StopCaptureAsync(CancellationToken cancellationToken = default);
    Task<InputEvent?> CaptureNextEventAsync(CancellationToken cancellationToken = default);
    bool IsCapturing { get; }
    event Action<InputEvent>? InputCaptured;
}

public interface IInputConsentManager
{
    bool IsConsented { get; }
    string? ConsentedSessionId { get; set; }
    DateTime? ConsentGrantedAtUtc { get; set; }
    void GrantConsent(string sessionId);
    void RevokeConsent();
    bool ValidateConsent(string sessionId);
}

public record InputDiagnostics
{
    public bool IsInjectionEnabled { get; set; }
    public bool IsConsented { get; set; }
    public int EventsInjected { get; set; }
    public int EventsFailed { get; set; }
    public DateTime? LastEventAtUtc { get; set; }
}
