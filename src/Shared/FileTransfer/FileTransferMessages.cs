using System.Security.Cryptography;
using System.Text;

namespace RemoteSupport.Shared.FileTransfer;

public enum FileTransferMessageType : byte
{
    FileOffer = 0x01,
    FileAccept = 0x02,
    FileReject = 0x03,
    FileChunk = 0x04,
    FileChunkAck = 0x05,
    FileComplete = 0x06,
    FileError = 0x07,
    TransferPause = 0x08,
    TransferResume = 0x09,
    TransferCancel = 0x0A
}

public enum FileTransferDirection : byte
{
    Upload = 0,
    Download = 1
}

public enum FileTransferStatus : byte
{
    Pending = 0,
    InProgress = 1,
    Paused = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5
}

public class FileTransferMessage
{
    public FileTransferMessageType Type { get; set; }
    public Guid TransferId { get; set; }

    // File offer
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public string? MimeType { get; set; }
    public FileTransferDirection Direction { get; set; }
    public bool PlaceOnClipboard { get; set; }
    public Guid BatchId { get; set; }
    public int BatchFileCount { get; set; }

    // Chunk
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public byte[]? ChunkData { get; set; }

    // Complete
    public string? Checksum { get; set; }

    // Error
    public string? ErrorMessage { get; set; }

    public static byte[] Serialize(FileTransferMessage msg)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)msg.Type);
        writer.Write(msg.TransferId.ToByteArray());

        switch (msg.Type)
        {
            case FileTransferMessageType.FileOffer:
                WriteString(writer, msg.FileName ?? "");
                writer.Write(msg.FileSize);
                WriteString(writer, msg.MimeType ?? "");
                writer.Write((byte)msg.Direction);
                writer.Write(msg.PlaceOnClipboard);
                writer.Write(msg.BatchId.ToByteArray());
                writer.Write(msg.BatchFileCount);
                break;

            case FileTransferMessageType.FileChunkAck:
                writer.Write(msg.ChunkIndex);
                break;

            case FileTransferMessageType.FileChunk:
                writer.Write(msg.ChunkIndex);
                writer.Write(msg.TotalChunks);
                writer.Write(msg.ChunkData?.Length ?? 0);
                if (msg.ChunkData is not null)
                    writer.Write(msg.ChunkData);
                break;

            case FileTransferMessageType.FileComplete:
                WriteString(writer, msg.Checksum ?? "");
                break;

            case FileTransferMessageType.FileError:
                WriteString(writer, msg.ErrorMessage ?? "");
                break;
        }

        return ms.ToArray();
    }

    public static FileTransferMessage? Deserialize(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            var msg = new FileTransferMessage
            {
                Type = (FileTransferMessageType)reader.ReadByte(),
                TransferId = new Guid(reader.ReadBytes(16))
            };

            switch (msg.Type)
            {
                case FileTransferMessageType.FileOffer:
                    msg.FileName = ReadString(reader);
                    msg.FileSize = reader.ReadInt64();
                    msg.MimeType = ReadString(reader);
                    msg.Direction = (FileTransferDirection)reader.ReadByte();
                    if (ms.Position < ms.Length)
                    {
                        msg.PlaceOnClipboard = reader.ReadBoolean();
                        if (ms.Length - ms.Position >= 16)
                            msg.BatchId = new Guid(reader.ReadBytes(16));
                        if (ms.Length - ms.Position >= 4)
                            msg.BatchFileCount = reader.ReadInt32();
                    }
                    break;

                case FileTransferMessageType.FileChunk:
                    msg.ChunkIndex = reader.ReadInt32();
                    msg.TotalChunks = reader.ReadInt32();
                    var chunkLen = reader.ReadInt32();
                    if (chunkLen > 0 && chunkLen <= FileTransferManager.ChunkSize + 1024)
                        msg.ChunkData = reader.ReadBytes(chunkLen);
                    break;

                case FileTransferMessageType.FileChunkAck:
                    if (ms.Position + 4 <= ms.Length)
                        msg.ChunkIndex = reader.ReadInt32();
                    break;

                case FileTransferMessageType.FileComplete:
                    msg.Checksum = ReadString(reader);
                    break;

                case FileTransferMessageType.FileError:
                    msg.ErrorMessage = ReadString(reader);
                    break;
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
        if (length <= 0 || length > 10_000_000) return "";
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    public static string ComputeChecksum(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    public static string ComputeFileChecksum(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
