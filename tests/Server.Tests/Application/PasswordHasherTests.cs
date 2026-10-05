using RemoteSupport.Server.Application.Services;

namespace RemoteSupport.Server.Tests.Application;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void HashPassword_ReturnsNonEmptyHash()
    {
        var hash = _hasher.HashPassword("TestPassword123!");

        Assert.False(string.IsNullOrEmpty(hash));
        Assert.NotEqual("TestPassword123!", hash);
    }

    [Fact]
    public void HashPassword_GeneratesDifferentHashesForSamePassword()
    {
        var hash1 = _hasher.HashPassword("SamePassword");
        var hash2 = _hasher.HashPassword("SamePassword");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void VerifyPassword_ReturnsTrue_WhenCorrect()
    {
        var password = "MySecureP@ssw0rd!";
        var hash = _hasher.HashPassword(password);

        Assert.True(_hasher.VerifyPassword(password, hash));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalse_WhenIncorrect()
    {
        var hash = _hasher.HashPassword("CorrectPassword");

        Assert.False(_hasher.VerifyPassword("WrongPassword", hash));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalse_WhenEmptyPassword()
    {
        var hash = _hasher.HashPassword("SomePassword");

        Assert.False(_hasher.VerifyPassword("", hash));
    }

    [Fact]
    public void HashPassword_HandlesSpecialCharacters()
    {
        var password = "!@#$%^&*()_+-=[]{}|;':\",./<>?`~";
        var hash = _hasher.HashPassword(password);

        Assert.True(_hasher.VerifyPassword(password, hash));
    }

    [Fact]
    public void HashPassword_HandlesUnicodeCharacters()
    {
        var password = "پسورد_یونیکد_123";
        var hash = _hasher.HashPassword(password);

        Assert.True(_hasher.VerifyPassword(password, hash));
    }
}
