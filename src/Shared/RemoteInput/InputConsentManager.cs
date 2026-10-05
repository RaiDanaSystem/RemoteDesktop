namespace RemoteSupport.Shared.RemoteInput;

public class InputConsentManager : IInputConsentManager
{
    private string? _consentedSessionId;
    private DateTime? _consentGrantedAtUtc;
    private readonly object _lock = new();

    public bool IsConsented
    {
        get { lock (_lock) return _consentedSessionId is not null; }
    }

    public string? ConsentedSessionId
    {
        get { lock (_lock) return _consentedSessionId; }
        set { lock (_lock) _consentedSessionId = value; }
    }

    public DateTime? ConsentGrantedAtUtc
    {
        get { lock (_lock) return _consentGrantedAtUtc; }
        set { lock (_lock) _consentGrantedAtUtc = value; }
    }

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
}
