using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportAgent.Services.Interfaces;

namespace SupportAgent.Services.Implementation;

public class TokenStorage : ITokenStorage
{
    private static readonly string StoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteSupportAgent",
        "tokens.dat");

    private class StoredTokens
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTime AccessTokenExpiresAtUtc { get; set; }
        public DateTime RefreshTokenExpiresAtUtc { get; set; }
        public string? Username { get; set; }
    }

    public void SaveTokens(string accessToken, string refreshToken, DateTime accessExpiry, DateTime refreshExpiry, string? username = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var tokens = new StoredTokens
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                AccessTokenExpiresAtUtc = accessExpiry,
                RefreshTokenExpiresAtUtc = refreshExpiry,
                Username = username
            };

            var json = JsonSerializer.Serialize(tokens);
            var data = Encoding.UTF8.GetBytes(json);
            var encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(StoragePath, encrypted);
        }
        catch (Exception)
        {
            // Silently fail — tokens won't persist across restarts
        }
    }

    public (string? AccessToken, string? RefreshToken, DateTime AccessExpiry, DateTime RefreshExpiry, string? Username)? LoadTokens()
    {
        try
        {
            if (!File.Exists(StoragePath)) return null;

            var encrypted = File.ReadAllBytes(StoragePath);
            var data = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(data);
            var tokens = JsonSerializer.Deserialize<StoredTokens>(json);

            if (tokens is null) return null;

            if (tokens.RefreshTokenExpiresAtUtc < DateTime.UtcNow)
            {
                ClearTokens();
                return null;
            }

            return (tokens.AccessToken, tokens.RefreshToken, tokens.AccessTokenExpiresAtUtc, tokens.RefreshTokenExpiresAtUtc, tokens.Username);
        }
        catch
        {
            return null;
        }
    }

    public void ClearTokens()
    {
        try
        {
            if (File.Exists(StoragePath))
                File.Delete(StoragePath);
        }
        catch { }
    }
}
