using RemoteSupport.Shared.RemoteInput;
using RemoteSupport.Shared.Clipboard;

namespace RemoteSupport.Server.Tests.Transport;

public class ConsentEnforcementTests
{
    [Fact]
    public void InputConsent_ValidateConsent_BeforeGranting_ReturnsFalse()
    {
        var consent = new InputConsentManager();
        Assert.False(consent.ValidateConsent("session-1"));
    }

    [Fact]
    public void InputConsent_ValidateConsent_AfterGranting_ReturnsTrue()
    {
        var consent = new InputConsentManager();
        consent.GrantConsent("session-1");
        Assert.True(consent.ValidateConsent("session-1"));
    }

    [Fact]
    public void InputConsent_ValidateConsent_WrongSession_ReturnsFalse()
    {
        var consent = new InputConsentManager();
        consent.GrantConsent("session-1");
        Assert.False(consent.ValidateConsent("session-2"));
    }

    [Fact]
    public void InputConsent_ValidateConsent_AfterRevoke_ReturnsFalse()
    {
        var consent = new InputConsentManager();
        consent.GrantConsent("session-1");
        consent.RevokeConsent();
        Assert.False(consent.ValidateConsent("session-1"));
    }

    [Fact]
    public void InputConsent_IsConsented_ReflectsState()
    {
        var consent = new InputConsentManager();
        Assert.False(consent.IsConsented);

        consent.GrantConsent("session-1");
        Assert.True(consent.IsConsented);

        consent.RevokeConsent();
        Assert.False(consent.IsConsented);
    }

    [Fact]
    public void ClipboardConsent_ValidateConsent_BeforeGranting_ReturnsFalse()
    {
        var consent = new ClipboardManager();
        Assert.False(consent.ValidateConsent("session-1"));
    }

    [Fact]
    public void ClipboardConsent_ValidateConsent_AfterGranting_ReturnsTrue()
    {
        var consent = new ClipboardManager();
        consent.GrantConsent("session-1");
        Assert.True(consent.ValidateConsent("session-1"));
    }

    [Fact]
    public void ClipboardConsent_ValidateConsent_AfterRevoke_ReturnsFalse()
    {
        var consent = new ClipboardManager();
        consent.GrantConsent("session-1");
        consent.RevokeConsent();
        Assert.False(consent.ValidateConsent("session-1"));
    }
}
