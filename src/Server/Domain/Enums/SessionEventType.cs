namespace RemoteSupport.Server.Domain.Enums;

public enum SessionEventType
{
    ConnectionRequested,
    ConnectionAccepted,
    ConnectionRejected,
    SessionStarted,
    SessionEnded,
    FileTransferStarted,
    FileTransferCompleted,
    FileTransferFailed,
    ClipboardSync,
    ChatMessage,
    ScreenShareStarted,
    ScreenShareStopped,
    RemoteInputStarted,
    RemoteInputStopped,
    Error
}
