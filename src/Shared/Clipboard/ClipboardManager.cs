namespace RemoteSupport.Shared.Clipboard;

public class ClipboardManager : IClipboardManager
{
    private string? _consentedSessionId;
    private DateTime? _consentGrantedAtUtc;
    private readonly object _lock = new();

    public bool IsConsented
    {
        get { lock (_lock) return _consentedSessionId is not null; }
    }

    public event Action<string>? TextReceived;
    public event Action<List<ClipboardFileEntry>>? FileListReceived;

    public void GrantConsent(string sessionId)
    {
        lock (_lock)
        {
            _consentedSessionId = sessionId;
            _consentGrantedAtUtc = DateTime.UtcNow;
        }
    }

    public void RevokeConsent()
    {
        lock (_lock)
        {
            _consentedSessionId = null;
            _consentGrantedAtUtc = null;
        }
    }

    public bool ValidateConsent(string sessionId)
    {
        lock (_lock)
        {
            return _consentedSessionId is not null && _consentedSessionId == sessionId;
        }
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var msg = new ClipboardMessage
        {
            Type = ClipboardMessageType.TextSync,
            Text = text
        };

        var serialized = ClipboardMessage.Serialize(msg);
        TextReceived?.Invoke(text);
        await Task.CompletedTask;
    }

    public async Task SendFileListAsync(List<ClipboardFileEntry> files, CancellationToken cancellationToken = default)
    {
        var msg = new ClipboardMessage
        {
            Type = ClipboardMessageType.FileListSync,
            Files = files
        };

        var serialized = ClipboardMessage.Serialize(msg);
        FileListReceived?.Invoke(files);
        await Task.CompletedTask;
    }

    public void HandleReceivedMessage(ClipboardMessage message)
    {
        if (message.Type == ClipboardMessageType.TextSync && message.Text is not null)
        {
            TextReceived?.Invoke(message.Text);
        }
        else if (message.Type == ClipboardMessageType.FileListSync && message.Files is not null)
        {
            FileListReceived?.Invoke(message.Files);
        }
    }
}
