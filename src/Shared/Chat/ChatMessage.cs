using System.Text;

namespace RemoteSupport.Shared.Chat;

public enum ChatMessageType : byte
{
    Text = 0x01,
    FileTransfer = 0x02,
    SystemNotification = 0x03,
    TypingIndicator = 0x04,
    ReadReceipt = 0x05
}

public enum ChatMessageStatus : byte
{
    Sent = 0,
    Delivered = 1,
    Read = 2,
    Failed = 3
}

public class ChatMessage
{
    public Guid MessageId { get; set; } = Guid.NewGuid();
    public string SessionId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderRole { get; set; } = string.Empty;
    public ChatMessageType Type { get; set; } = ChatMessageType.Text;
    public string Content { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public ChatMessageStatus Status { get; set; } = ChatMessageStatus.Sent;

    // File transfer metadata
    public string? FileName { get; set; }
    public long? FileSize { get; set; }
    public string? FileTransferId { get; set; }

    public static byte[] Serialize(ChatMessage msg)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(msg.MessageId.ToByteArray());
        WriteString(writer, msg.SessionId);
        WriteString(writer, msg.SenderId);
        WriteString(writer, msg.SenderName);
        WriteString(writer, msg.SenderRole);
        writer.Write((byte)msg.Type);
        WriteString(writer, msg.Content);
        writer.Write(msg.TimestampUtc.Ticks);
        writer.Write((byte)msg.Status);

        if (msg.Type == ChatMessageType.FileTransfer)
        {
            WriteString(writer, msg.FileName ?? "");
            writer.Write(msg.FileSize ?? 0);
            WriteString(writer, msg.FileTransferId ?? "");
        }

        return ms.ToArray();
    }

    public static ChatMessage? Deserialize(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            var msg = new ChatMessage
            {
                MessageId = new Guid(reader.ReadBytes(16)),
                SessionId = ReadString(reader),
                SenderId = ReadString(reader),
                SenderName = ReadString(reader),
                SenderRole = ReadString(reader),
                Type = (ChatMessageType)reader.ReadByte(),
                Content = ReadString(reader),
                TimestampUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
                Status = (ChatMessageStatus)reader.ReadByte()
            };

            if (msg.Type == ChatMessageType.FileTransfer)
            {
                msg.FileName = ReadString(reader);
                msg.FileSize = reader.ReadInt64();
                msg.FileTransferId = ReadString(reader);
            }

            return msg;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length <= 0 || length > 1_000_000) return "";
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }
}
