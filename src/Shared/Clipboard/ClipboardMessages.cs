using System.Text;

namespace RemoteSupport.Shared.Clipboard;

public enum ClipboardMessageType : byte
{
    TextSync = 0x01,
    FileListSync = 0x02,
    ConsentGrant = 0x03,
    ConsentRevoke = 0x04,
    SyncRequest = 0x05
}

public class ClipboardMessage
{
    public ClipboardMessageType Type { get; set; }
    public string? Text { get; set; }
    public List<ClipboardFileEntry>? Files { get; set; }

    public static byte[] Serialize(ClipboardMessage msg)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)msg.Type);

        if (msg.Type == ClipboardMessageType.TextSync && msg.Text is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(msg.Text);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        if (msg.Files is not null)
        {
            writer.Write(msg.Files.Count);
            foreach (var file in msg.Files)
            {
                var nameBytes = Encoding.UTF8.GetBytes(file.FileName);
                writer.Write(nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write(file.Size);
                var hashBytes = Encoding.UTF8.GetBytes(file.Checksum ?? "");
                writer.Write(hashBytes.Length);
                writer.Write(hashBytes);
            }
        }

        return ms.ToArray();
    }

    public static ClipboardMessage? Deserialize(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            var msg = new ClipboardMessage
            {
                Type = (ClipboardMessageType)reader.ReadByte()
            };

            if (msg.Type == ClipboardMessageType.TextSync)
            {
                var textLen = reader.ReadInt32();
                if (textLen > 0 && textLen < 10_000_000)
                    msg.Text = Encoding.UTF8.GetString(reader.ReadBytes(textLen));
            }

            if (ms.Position < ms.Length)
            {
                var fileCount = reader.ReadInt32();
                msg.Files = new List<ClipboardFileEntry>();
                for (int i = 0; i < fileCount && i < 100; i++)
                {
                    var nameLen = reader.ReadInt32();
                    var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
                    var size = reader.ReadInt64();
                    var hashLen = reader.ReadInt32();
                    var hash = Encoding.UTF8.GetString(reader.ReadBytes(hashLen));
                    msg.Files.Add(new ClipboardFileEntry { FileName = name, Size = size, Checksum = hash });
                }
            }

            return msg;
        }
        catch
        {
            return null;
        }
    }
}

public class ClipboardFileEntry
{
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? Checksum { get; set; }
}

public interface IClipboardManager
{
    bool IsConsented { get; }
    void GrantConsent(string sessionId);
    void RevokeConsent();
    bool ValidateConsent(string sessionId);
    Task SendTextAsync(string text, CancellationToken cancellationToken = default);
    Task SendFileListAsync(List<ClipboardFileEntry> files, CancellationToken cancellationToken = default);
    event Action<string>? TextReceived;
    event Action<List<ClipboardFileEntry>>? FileListReceived;
}
