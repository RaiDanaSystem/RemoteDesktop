namespace RemoteSupport.Server.Application.DTOs;

public record LoginRequest(string Username, string Password, string? DeviceIdentifier = null);
public record LoginResponse(AccessTokenResponse AccessToken, RefreshTokenResponse RefreshToken, UserInfo User);
public record AccessTokenResponse(string Token, DateTime ExpiresAtUtc);
public record RefreshTokenResponse(string Token, DateTime ExpiresAtUtc);
public record UserInfo(Guid Id, string Username, string Email, string Role, string? DisplayName);
public record RefreshTokenRequest(string RefreshToken);
public record RegisterRequest(string Username, string Email, string Password, string? DisplayName = null);
public record LogoutRequest(string? RefreshToken = null);

public record CustomerAuthRequest(string SupportCode, string DeviceIdentifier, string? DeviceName = null, string? OperatingSystem = null);
public record CustomerAuthResponse(AccessTokenResponse AccessToken, SessionInfo Session);
public record SessionInfo(Guid SessionId, string Status);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
