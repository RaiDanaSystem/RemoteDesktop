using RemoteSupport.Shared.Chat;

namespace RemoteSupport.Server.Tests.Chat;

public class ChatTests : IDisposable
{
    private readonly ChatManager _chatManager;

    public ChatTests()
    {
        _chatManager = new ChatManager();
    }

    public void Dispose() { }

    // --- Message Serialization Tests ---

    [Fact]
    public void ChatMessage_Text_SerializeDeserialize()
    {
        var msg = new ChatMessage
        {
            MessageId = Guid.NewGuid(),
            SessionId = "session-1",
            SenderId = "user-1",
            SenderName = "Agent",
            SenderRole = "SupportAgent",
            Type = ChatMessageType.Text,
            Content = "Hello!",
            TimestampUtc = DateTime.UtcNow,
            Status = ChatMessageStatus.Sent
        };

        var serialized = ChatMessage.Serialize(msg);
        var deserialized = ChatMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(msg.MessageId, deserialized!.MessageId);
        Assert.Equal("session-1", deserialized.SessionId);
        Assert.Equal("user-1", deserialized.SenderId);
        Assert.Equal("Agent", deserialized.SenderName);
        Assert.Equal("SupportAgent", deserialized.SenderRole);
        Assert.Equal(ChatMessageType.Text, deserialized.Type);
        Assert.Equal("Hello!", deserialized.Content);
        Assert.Equal(ChatMessageStatus.Sent, deserialized.Status);
    }

    [Fact]
    public void ChatMessage_PersianText_SerializeDeserialize()
    {
        var persianContent = "سلام! این یک پیام فارسی است. چطور می‌توانم کمک کنم؟";
        var msg = new ChatMessage
        {
            MessageId = Guid.NewGuid(),
            SessionId = "session-1",
            SenderId = "agent-1",
            SenderName = "Agent",
            SenderRole = "SupportAgent",
            Content = persianContent
        };

        var serialized = ChatMessage.Serialize(msg);
        var deserialized = ChatMessage.Deserialize(serialized);

        Assert.Equal(persianContent, deserialized!.Content);
    }

    [Fact]
    public void ChatMessage_EmptyContent_SerializeDeserialize()
    {
        var msg = new ChatMessage
        {
            MessageId = Guid.NewGuid(),
            SessionId = "session-1",
            SenderId = "user-1",
            SenderName = "User",
            SenderRole = "Customer",
            Content = ""
        };

        var serialized = ChatMessage.Serialize(msg);
        var deserialized = ChatMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal("", deserialized!.Content);
    }

    [Fact]
    public void ChatMessage_LargeContent_SerializeDeserialize()
    {
        var largeContent = new string('X', 50_000);
        var msg = new ChatMessage
        {
            MessageId = Guid.NewGuid(),
            SessionId = "session-1",
            SenderId = "user-1",
            SenderName = "User",
            SenderRole = "Customer",
            Content = largeContent
        };

        var serialized = ChatMessage.Serialize(msg);
        var deserialized = ChatMessage.Deserialize(serialized);

        Assert.Equal(50_000, deserialized!.Content.Length);
    }

    [Fact]
    public void ChatMessage_FileTransfer_SerializeDeserialize()
    {
        var msg = new ChatMessage
        {
            MessageId = Guid.NewGuid(),
            SessionId = "session-1",
            SenderId = "agent-1",
            SenderName = "Agent",
            SenderRole = "SupportAgent",
            Type = ChatMessageType.FileTransfer,
            Content = "Sending file: report.pdf",
            FileName = "report.pdf",
            FileSize = 1024000,
            FileTransferId = "transfer-abc"
        };

        var serialized = ChatMessage.Serialize(msg);
        var deserialized = ChatMessage.Deserialize(serialized);

        Assert.Equal(ChatMessageType.FileTransfer, deserialized!.Type);
        Assert.Equal("report.pdf", deserialized.FileName);
        Assert.Equal(1024000, deserialized.FileSize);
        Assert.Equal("transfer-abc", deserialized.FileTransferId);
    }

    [Fact]
    public void ChatMessage_AllMessageTypes_SerializeDeserialize()
    {
        foreach (var type in Enum.GetValues<ChatMessageType>())
        {
            var msg = new ChatMessage
            {
                MessageId = Guid.NewGuid(),
                SessionId = "session-1",
                SenderId = "user-1",
                SenderName = "User",
                SenderRole = "Customer",
                Type = type,
                Content = $"Test {type}"
            };

            var serialized = ChatMessage.Serialize(msg);
            var deserialized = ChatMessage.Deserialize(serialized);

            Assert.Equal(type, deserialized!.Type);
        }
    }

    [Fact]
    public void ChatMessage_AllStatuses_SerializeDeserialize()
    {
        foreach (var status in Enum.GetValues<ChatMessageStatus>())
        {
            var msg = new ChatMessage
            {
                MessageId = Guid.NewGuid(),
                SessionId = "session-1",
                SenderId = "user-1",
                SenderName = "User",
                SenderRole = "Customer",
                Status = status
            };

            var serialized = ChatMessage.Serialize(msg);
            var deserialized = ChatMessage.Deserialize(serialized);

            Assert.Equal(status, deserialized!.Status);
        }
    }

    [Fact]
    public void ChatMessage_Timestamp_Preserved()
    {
        var timestamp = new DateTime(2025, 6, 15, 14, 30, 0, DateTimeKind.Utc);
        var msg = new ChatMessage
        {
            MessageId = Guid.NewGuid(),
            SessionId = "session-1",
            SenderId = "user-1",
            SenderName = "User",
            SenderRole = "Customer",
            TimestampUtc = timestamp
        };

        var serialized = ChatMessage.Serialize(msg);
        var deserialized = ChatMessage.Deserialize(serialized);

        Assert.Equal(timestamp, deserialized!.TimestampUtc);
    }

    [Fact]
    public void ChatMessage_InvalidData_ReturnsNull()
    {
        var result = ChatMessage.Deserialize(new byte[] { 0xFF, 0xFF, 0xFF });
        Assert.Null(result);
    }

    // --- Chat Manager Tests ---

    [Fact]
    public void ChatManager_AddMessage_ReturnsMessage()
    {
        var msg = _chatManager.AddMessage("session-1", "agent-1", "Agent", "SupportAgent", "Hello!");

        Assert.NotEqual(Guid.Empty, msg.MessageId);
        Assert.Equal("session-1", msg.SessionId);
        Assert.Equal("Hello!", msg.Content);
        Assert.Equal(ChatMessageStatus.Sent, msg.Status);
    }

    [Fact]
    public void ChatManager_GetHistory_ReturnsMessages()
    {
        _chatManager.AddMessage("session-1", "agent-1", "Agent", "SupportAgent", "msg1");
        _chatManager.AddMessage("session-1", "customer-1", "Customer", "Customer", "msg2");
        _chatManager.AddMessage("session-1", "agent-1", "Agent", "SupportAgent", "msg3");

        var history = _chatManager.GetSessionHistory("session-1");

        Assert.Equal(3, history.Count);
        Assert.Equal("msg1", history[0].Content);
        Assert.Equal("msg3", history[2].Content);
    }

    [Fact]
    public void ChatManager_GetHistory_LimitWorks()
    {
        for (int i = 0; i < 50; i++)
        {
            _chatManager.AddMessage("session-1", "user", "User", "Customer", $"msg{i}");
        }

        var history = _chatManager.GetSessionHistory("session-1", limit: 10);

        Assert.Equal(10, history.Count);
        Assert.Equal("msg40", history[0].Content);
        Assert.Equal("msg49", history[9].Content);
    }

    [Fact]
    public void ChatManager_SessionIsolation()
    {
        _chatManager.AddMessage("session-1", "agent-1", "Agent", "SupportAgent", "msg for session 1");
        _chatManager.AddMessage("session-2", "agent-1", "Agent", "SupportAgent", "msg for session 2");

        var history1 = _chatManager.GetSessionHistory("session-1");
        var history2 = _chatManager.GetSessionHistory("session-2");

        Assert.Single(history1);
        Assert.Single(history2);
        Assert.Equal("msg for session 1", history1[0].Content);
        Assert.Equal("msg for session 2", history2[0].Content);
    }

    [Fact]
    public void ChatManager_SessionIsolation_EmptySession()
    {
        var history = _chatManager.GetSessionHistory("nonexistent-session");

        Assert.Empty(history);
    }

    [Fact]
    public void ChatManager_MarkAsDelivered()
    {
        var msg = _chatManager.AddMessage("session-1", "agent-1", "Agent", "SupportAgent", "Hello!");

        _chatManager.MarkAsDelivered(msg.MessageId, "session-1");

        var history = _chatManager.GetSessionHistory("session-1");
        Assert.Equal(ChatMessageStatus.Delivered, history[0].Status);
    }

    [Fact]
    public void ChatManager_MarkAsRead()
    {
        var msg = _chatManager.AddMessage("session-1", "agent-1", "Agent", "SupportAgent", "Hello!");

        _chatManager.MarkAsRead(msg.MessageId, "session-1");

        var history = _chatManager.GetSessionHistory("session-1");
        Assert.Equal(ChatMessageStatus.Read, history[0].Status);
    }

    [Fact]
    public void ChatManager_GetUnreadCount()
    {
        _chatManager.AddMessage("session-1", "agent-1", "Agent", "SupportAgent", "msg1");
        _chatManager.AddMessage("session-1", "agent-1", "Agent", "SupportAgent", "msg2");
        _chatManager.AddMessage("session-1", "customer-1", "Customer", "Customer", "msg3");

        var unread = _chatManager.GetUnreadCount("session-1", "customer-1");

        Assert.Equal(2, unread); // 2 messages from agent not delivered yet
    }

    [Fact]
    public void ChatManager_GetUnreadCount_ExcludesOwnMessages()
    {
        _chatManager.AddMessage("session-1", "customer-1", "Customer", "Customer", "msg1");

        var unread = _chatManager.GetUnreadCount("session-1", "customer-1");

        Assert.Equal(0, unread); // Own messages don't count
    }

    [Fact]
    public void ChatManager_ClearSession()
    {
        _chatManager.AddMessage("session-1", "agent-1", "Agent", "SupportAgent", "msg1");
        _chatManager.ClearSession("session-1");

        var history = _chatManager.GetSessionHistory("session-1");
        Assert.Empty(history);
    }

    [Fact]
    public void ChatManager_MessageReceived_Event()
    {
        ChatMessage? receivedMessage = null;
        _chatManager.MessageReceived += msg => receivedMessage = msg;

        var msg = _chatManager.AddMessage("session-1", "agent-1", "Agent", "SupportAgent", "Hello!");

        Assert.NotNull(receivedMessage);
        Assert.Equal(msg.MessageId, receivedMessage!.MessageId);
        Assert.Equal("Hello!", receivedMessage.Content);
    }

    [Fact]
    public void ChatManager_PersianMessage_StoredCorrectly()
    {
        var persian = "سلام، چطور می‌توانم کمک کنم؟";
        _chatManager.AddMessage("session-1", "agent-1", "Agent", "SupportAgent", persian);

        var history = _chatManager.GetSessionHistory("session-1");
        Assert.Equal(persian, history[0].Content);
    }

    [Fact]
    public void ChatManager_MessageOrdering_IsChronological()
    {
        for (int i = 0; i < 20; i++)
        {
            _chatManager.AddMessage("session-1", "user", "User", "Customer", $"msg-{i:D3}");
        }

        var history = _chatManager.GetSessionHistory("session-1");

        for (int i = 1; i < history.Count; i++)
        {
            Assert.True(history[i].TimestampUtc >= history[i - 1].TimestampUtc);
        }
    }

    // --- Thread Safety Tests ---

    [Fact]
    public void ChatManager_ThreadSafety_ConcurrentMessages()
    {
        var tasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() =>
            {
                _chatManager.AddMessage("session-1", $"user-{idx}", $"User{idx}", "Customer", $"msg-{idx}");
            }));
        }

        Task.WaitAll(tasks.ToArray());

        var history = _chatManager.GetSessionHistory("session-1", 200);
        Assert.Equal(100, history.Count);
    }

    [Fact]
    public void ChatManager_ThreadSafety_ConcurrentSessions()
    {
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() =>
            {
                _chatManager.AddMessage($"session-{idx}", "user", "User", "Customer", $"msg in session {idx}");
            }));
        }

        Task.WaitAll(tasks.ToArray());

        for (int i = 0; i < 50; i++)
        {
            var history = _chatManager.GetSessionHistory($"session-{i}");
            Assert.Single(history);
        }
    }
}
