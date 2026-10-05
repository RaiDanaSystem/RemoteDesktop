using RemoteSupport.Server.Domain.Entities;
using RemoteSupport.Server.Domain.Enums;

namespace RemoteSupport.Server.Tests.Domain;

public class EntityBaseTests
{
    [Fact]
    public void User_HasDefaultValues()
    {
        var user = new User();

        Assert.Equal(Guid.Empty, user.Id);
        Assert.Equal(string.Empty, user.Username);
        Assert.True(user.IsActive);
        Assert.Equal(UserRole.SupportAgent, user.Role);
    }

    [Fact]
    public void Session_HasDefaultStatus()
    {
        var session = new Session();

        Assert.Equal(SessionStatus.Pending, session.Status);
        Assert.Null(session.StartedAtUtc);
        Assert.Null(session.EndedAtUtc);
    }

    [Fact]
    public void SupportCode_CanBeCreated()
    {
        var code = new SupportCode
        {
            Id = Guid.NewGuid(),
            Code = "ABC12345",
            DeviceIdentifier = "device-001",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
            IsUsed = false,
            CreatedAtUtc = DateTime.UtcNow
        };

        Assert.NotEqual(Guid.Empty, code.Id);
        Assert.Equal("ABC12345", code.Code);
        Assert.False(code.IsUsed);
    }

    [Fact]
    public void SessionEvent_HasCorrectDefaults()
    {
        var sessionEvent = new SessionEvent
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            EventType = SessionEventType.ConnectionRequested,
            OccurredAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };

        Assert.Equal(SessionEventType.ConnectionRequested, sessionEvent.EventType);
    }

    [Fact]
    public void AuditLog_HasCorrectDefaults()
    {
        var auditLog = new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = AuditAction.UserLogin,
            OccurredAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };

        Assert.Equal(AuditAction.UserLogin, auditLog.Action);
    }

    [Fact]
    public void FileTransferMetadata_HasCorrectDefaults()
    {
        var transfer = new FileTransferMetadata
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            FileName = "test.txt",
            FileSizeBytes = 1024,
            IsUpload = true,
            CreatedAtUtc = DateTime.UtcNow
        };

        Assert.Equal("test.txt", transfer.FileName);
        Assert.Equal(1024, transfer.FileSizeBytes);
        Assert.True(transfer.IsUpload);
    }
}
