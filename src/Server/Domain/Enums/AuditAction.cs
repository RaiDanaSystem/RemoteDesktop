namespace RemoteSupport.Server.Domain.Enums;

public enum AuditAction
{
    UserLogin,
    UserLogout,
    UserLoginFailed,
    SessionCreated,
    SessionConnected,
    SessionDisconnected,
    SupportCodeGenerated,
    SupportCodeUsed,
    FileTransferInitiated,
    FileTransferCompleted,
    PermissionChanged,
    UserCreated,
    UserDisabled,
    SettingsChanged,
    SecurityEvent
}
