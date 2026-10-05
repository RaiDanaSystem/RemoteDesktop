using System.Collections.Concurrent;

namespace RemoteSupport.Shared.Chat;

public interface IChatManager
{
    ChatMessage AddMessage(string sessionId, string senderId, string senderName, string senderRole, string content, ChatMessageType type = ChatMessageType.Text);
    IReadOnlyList<ChatMessage> GetSessionHistory(string sessionId, int limit = 100);
    void MarkAsDelivered(Guid messageId, string sessionId);
    void MarkAsRead(Guid messageId, string sessionId);
    int GetUnreadCount(string sessionId, string recipientId);
    void ClearSession(string sessionId);
    event Action<ChatMessage>? MessageReceived;
}

public class ChatManager : IChatManager
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _sessionMessages = new();
    private readonly object _lock = new();

    public event Action<ChatMessage>? MessageReceived;

    public ChatMessage AddMessage(
        string sessionId, string senderId, string senderName,
        string senderRole, string content,
        ChatMessageType type = ChatMessageType.Text)
    {
        var message = new ChatMessage
        {
            SessionId = sessionId,
            SenderId = senderId,
            SenderName = senderName,
            SenderRole = senderRole,
            Content = content,
            Type = type,
            TimestampUtc = DateTime.UtcNow,
            Status = ChatMessageStatus.Sent
        };

        var messages = _sessionMessages.GetOrAdd(sessionId, _ => new List<ChatMessage>());
        lock (_lock)
        {
            messages.Add(message);
        }

        MessageReceived?.Invoke(message);
        return message;
    }

    public IReadOnlyList<ChatMessage> GetSessionHistory(string sessionId, int limit = 100)
    {
        if (!_sessionMessages.TryGetValue(sessionId, out var messages))
            return Array.Empty<ChatMessage>();

        lock (_lock)
        {
            return messages.TakeLast(limit).ToList().AsReadOnly();
        }
    }

    public void MarkAsDelivered(Guid messageId, string sessionId)
    {
        UpdateMessageStatus(messageId, sessionId, ChatMessageStatus.Delivered);
    }

    public void MarkAsRead(Guid messageId, string sessionId)
    {
        UpdateMessageStatus(messageId, sessionId, ChatMessageStatus.Read);
    }

    public int GetUnreadCount(string sessionId, string recipientId)
    {
        if (!_sessionMessages.TryGetValue(sessionId, out var messages))
            return 0;

        lock (_lock)
        {
            return messages.Count(m =>
                m.SenderId != recipientId && m.Status < ChatMessageStatus.Delivered);
        }
    }

    public void ClearSession(string sessionId)
    {
        _sessionMessages.TryRemove(sessionId, out _);
    }

    private void UpdateMessageStatus(Guid messageId, string sessionId, ChatMessageStatus status)
    {
        if (!_sessionMessages.TryGetValue(sessionId, out var messages))
            return;

        lock (_lock)
        {
            var message = messages.FirstOrDefault(m => m.MessageId == messageId);
            if (message is not null)
            {
                message.Status = status;
            }
        }
    }
}
