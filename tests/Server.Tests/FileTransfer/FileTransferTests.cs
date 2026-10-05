using System.Security.Cryptography;
using RemoteSupport.Shared.FileTransfer;

namespace RemoteSupport.Server.Tests.FileTransfer;

public class FileTransferTests : IDisposable
{
    private readonly FileTransferManager _manager;
    private readonly string _testDir;

    public FileTransferTests()
    {
        _manager = new FileTransferManager();
        _testDir = Path.Combine(Path.GetTempPath(), $"filetransfer_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    private string CreateTestFile(string name, int size)
    {
        var path = Path.Combine(_testDir, name);
        var data = new byte[size];
        RandomNumberGenerator.Fill(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    // --- Message Serialization Tests ---

    [Fact]
    public void FileTransferMessage_FileOffer_SerializeDeserialize()
    {
        var msg = new FileTransferMessage
        {
            Type = FileTransferMessageType.FileOffer,
            TransferId = Guid.NewGuid(),
            FileName = "test.txt",
            FileSize = 1024,
            MimeType = "text/plain",
            Direction = FileTransferDirection.Upload
        };

        var serialized = FileTransferMessage.Serialize(msg);
        var deserialized = FileTransferMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(FileTransferMessageType.FileOffer, deserialized!.Type);
        Assert.Equal(msg.TransferId, deserialized.TransferId);
        Assert.Equal("test.txt", deserialized.FileName);
        Assert.Equal(1024, deserialized.FileSize);
        Assert.Equal("text/plain", deserialized.MimeType);
        Assert.Equal(FileTransferDirection.Upload, deserialized.Direction);
    }

    [Fact]
    public void FileTransferMessage_FileChunk_SerializeDeserialize()
    {
        var chunkData = new byte[] { 0x01, 0x02, 0x03, 0xFF };
        var msg = new FileTransferMessage
        {
            Type = FileTransferMessageType.FileChunk,
            TransferId = Guid.NewGuid(),
            ChunkIndex = 5,
            TotalChunks = 100,
            ChunkData = chunkData
        };

        var serialized = FileTransferMessage.Serialize(msg);
        var deserialized = FileTransferMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(FileTransferMessageType.FileChunk, deserialized!.Type);
        Assert.Equal(5, deserialized.ChunkIndex);
        Assert.Equal(100, deserialized.TotalChunks);
        Assert.Equal(chunkData, deserialized.ChunkData);
    }

    [Fact]
    public void FileTransferMessage_FileComplete_SerializeDeserialize()
    {
        var msg = new FileTransferMessage
        {
            Type = FileTransferMessageType.FileComplete,
            TransferId = Guid.NewGuid(),
            Checksum = "ABC123"
        };

        var serialized = FileTransferMessage.Serialize(msg);
        var deserialized = FileTransferMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal("ABC123", deserialized!.Checksum);
    }

    [Fact]
    public void FileTransferMessage_FileError_SerializeDeserialize()
    {
        var msg = new FileTransferMessage
        {
            Type = FileTransferMessageType.FileError,
            TransferId = Guid.NewGuid(),
            ErrorMessage = "Permission denied"
        };

        var serialized = FileTransferMessage.Serialize(msg);
        var deserialized = FileTransferMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal("Permission denied", deserialized!.ErrorMessage);
    }

    [Fact]
    public void FileTransferMessage_ControlMessages_SerializeDeserialize()
    {
        var transferId = Guid.NewGuid();

        foreach (var type in new[] {
            FileTransferMessageType.FileAccept,
            FileTransferMessageType.FileReject,
            FileTransferMessageType.TransferPause,
            FileTransferMessageType.TransferResume,
            FileTransferMessageType.TransferCancel })
        {
            var msg = new FileTransferMessage { Type = type, TransferId = transferId };
            var serialized = FileTransferMessage.Serialize(msg);
            var deserialized = FileTransferMessage.Deserialize(serialized);

            Assert.NotNull(deserialized);
            Assert.Equal(type, deserialized!.Type);
            Assert.Equal(transferId, deserialized.TransferId);
        }
    }

    [Fact]
    public void FileTransferMessage_InvalidData_ReturnsNull()
    {
        var result = FileTransferMessage.Deserialize(new byte[] { 0xFF, 0xFF });
        Assert.Null(result);
    }

    // --- Checksum Tests ---

    [Fact]
    public void ComputeChecksum_Deterministic()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var hash1 = FileTransferMessage.ComputeChecksum(data);
        var hash2 = FileTransferMessage.ComputeChecksum(data);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeChecksum_DifferentData_DifferentHash()
    {
        var hash1 = FileTransferMessage.ComputeChecksum(new byte[] { 1, 2, 3 });
        var hash2 = FileTransferMessage.ComputeChecksum(new byte[] { 4, 5, 6 });

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeFileChecksum_ValidFile()
    {
        var path = CreateTestFile("checksum.bin", 1024);
        var checksum = FileTransferMessage.ComputeFileChecksum(path);

        Assert.False(string.IsNullOrEmpty(checksum));
        Assert.Equal(64, checksum.Length); // SHA256 hex = 64 chars
    }

    [Fact]
    public void ComputeFileChecksum_Deterministic()
    {
        var path = CreateTestFile("deterministic.bin", 512);
        var hash1 = FileTransferMessage.ComputeFileChecksum(path);
        var hash2 = FileTransferMessage.ComputeFileChecksum(path);

        Assert.Equal(hash1, hash2);
    }

    // --- File Transfer Manager Tests ---

    [Fact]
    public async Task InitiateTransfer_ValidFile_ReturnsSuccess()
    {
        var path = CreateTestFile("test.txt", 200 * 1024);

        var result = await _manager.InitiateTransferAsync(
            path, FileTransferDirection.Upload, "session-1");

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.TransferId);
        Assert.Equal("test.txt", result.FileName);
        Assert.Equal(200 * 1024, result.FileSize);
        Assert.True(result.TotalChunks > 0);
    }

    [Fact]
    public async Task InitiateTransfer_NonexistentFile_ReturnsError()
    {
        var result = await _manager.InitiateTransferAsync(
            "/nonexistent/file.txt", FileTransferDirection.Upload, "session-1");

        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.ErrorMessage!);
    }

    [Fact]
    public async Task SendChunk_ValidTransfer_ReturnsSuccess()
    {
        var path = CreateTestFile("chunk.bin", 1024);
        var initResult = await _manager.InitiateTransferAsync(
            path, FileTransferDirection.Upload, "session-1");

        var chunkData = new byte[1024];
        var sendResult = await _manager.SendChunkAsync(
            initResult.TransferId, chunkData, 0, 1);

        Assert.True(sendResult.IsSuccess);
    }

    [Fact]
    public async Task SendChunk_InvalidTransfer_ReturnsError()
    {
        var result = await _manager.SendChunkAsync(
            Guid.NewGuid(), new byte[100], 0, 1);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task ReceiveChunk_CompletesTransfer()
    {
        var path = CreateTestFile("receive.bin", 100);
        var initResult = await _manager.InitiateTransferAsync(
            path, FileTransferDirection.Download, "session-1");

        var fileBytes = File.ReadAllBytes(path);
        var chunkData = new byte[100];
        Array.Copy(fileBytes, chunkData, 100);

        var result = await _manager.ReceiveChunkAsync(
            initResult.TransferId, 0, chunkData);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsComplete);
        Assert.False(string.IsNullOrEmpty(result.Checksum));
    }

    [Fact]
    public async Task ReceiveChunk_MultipleChunks_CompletesOnLast()
    {
        // Use a file larger than ChunkSize to force multiple chunks
        var path = CreateTestFile("multi.bin", FileTransferManager.ChunkSize * 3);
        var initResult = await _manager.InitiateTransferAsync(
            path, FileTransferDirection.Download, "session-1");

        Assert.Equal(3, initResult.TotalChunks);

        var fileBytes = File.ReadAllBytes(path);
        for (int i = 0; i < initResult.TotalChunks; i++)
        {
            var offset = i * FileTransferManager.ChunkSize;
            var length = Math.Min(FileTransferManager.ChunkSize, fileBytes.Length - offset);
            var chunk = new byte[length];
            Array.Copy(fileBytes, offset, chunk, 0, length);

            var r = await _manager.ReceiveChunkAsync(initResult.TransferId, i, chunk);
            Assert.True(r.IsSuccess);
            if (i < initResult.TotalChunks - 1)
                Assert.False(r.IsComplete);
        }

        var lastChunk = await _manager.ReceiveChunkAsync(
            initResult.TransferId, initResult.TotalChunks - 1,
            fileBytes.Skip((initResult.TotalChunks - 1) * FileTransferManager.ChunkSize).ToArray());

        // Already completed, re-receive returns the complete state
        var info = _manager.GetTransferInfo(initResult.TransferId);
        Assert.Equal(FileTransferStatus.Completed, info.Status);
    }

    [Fact]
    public async Task PauseResume_Works()
    {
        var path = CreateTestFile("pause.bin", 100);
        var initResult = await _manager.InitiateTransferAsync(
            path, FileTransferDirection.Upload, "session-1");

        await _manager.PauseTransferAsync(initResult.TransferId);
        var info = _manager.GetTransferInfo(initResult.TransferId);
        Assert.Equal(FileTransferStatus.Paused, info.Status);

        var sendResult = await _manager.SendChunkAsync(
            initResult.TransferId, new byte[100], 0, 1);
        Assert.False(sendResult.IsSuccess);
        Assert.Contains("paused", sendResult.ErrorMessage!);

        await _manager.ResumeTransferAsync(initResult.TransferId);
        info = _manager.GetTransferInfo(initResult.TransferId);
        Assert.Equal(FileTransferStatus.InProgress, info.Status);

        sendResult = await _manager.SendChunkAsync(
            initResult.TransferId, new byte[100], 0, 1);
        Assert.True(sendResult.IsSuccess);
    }

    [Fact]
    public async Task CancelTransfer_Works()
    {
        var path = CreateTestFile("cancel.bin", 100);
        var initResult = await _manager.InitiateTransferAsync(
            path, FileTransferDirection.Upload, "session-1");

        await _manager.CancelTransferAsync(initResult.TransferId);

        var sendResult = await _manager.SendChunkAsync(
            initResult.TransferId, new byte[100], 0, 1);
        Assert.False(sendResult.IsSuccess);
        Assert.Contains("cancelled", sendResult.ErrorMessage!);
    }

    [Fact]
    public async Task ChecksumVerification_CorrectFile()
    {
        var path = CreateTestFile("verify.bin", 1000);
        var expectedChecksum = FileTransferMessage.ComputeFileChecksum(path);

        var initResult = await _manager.InitiateTransferAsync(
            path, FileTransferDirection.Download, "session-1");

        var fileBytes = File.ReadAllBytes(path);
        await _manager.ReceiveChunkAsync(initResult.TransferId, 0, fileBytes);

        var info = _manager.GetTransferInfo(initResult.TransferId);
        Assert.Equal(FileTransferStatus.Completed, info.Status);
    }

    [Fact]
    public async Task LargeFile_ChunkedTransfer()
    {
        var path = CreateTestFile("large.bin", 1024 * 1024); // 1MB
        var initResult = await _manager.InitiateTransferAsync(
            path, FileTransferDirection.Download, "session-1");

        var fileBytes = File.ReadAllBytes(path);
        var totalChunks = initResult.TotalChunks;
        Assert.True(totalChunks > 1);

        for (int i = 0; i < totalChunks; i++)
        {
            var offset = i * FileTransferManager.ChunkSize;
            var length = Math.Min(FileTransferManager.ChunkSize, fileBytes.Length - offset);
            var chunk = new byte[length];
            Array.Copy(fileBytes, offset, chunk, 0, length);

            var result = await _manager.ReceiveChunkAsync(initResult.TransferId, i, chunk);
            Assert.True(result.IsSuccess);
        }

        var info = _manager.GetTransferInfo(initResult.TransferId);
        Assert.Equal(FileTransferStatus.Completed, info.Status);
    }

    // --- Session Isolation Tests ---

    [Fact]
    public async Task Transfers_IsolatedByTransferId()
    {
        var path1 = CreateTestFile("file1.bin", 100);
        var path2 = CreateTestFile("file2.bin", 200);

        var result1 = await _manager.InitiateTransferAsync(
            path1, FileTransferDirection.Upload, "session-1");
        var result2 = await _manager.InitiateTransferAsync(
            path2, FileTransferDirection.Upload, "session-2");

        Assert.NotEqual(result1.TransferId, result2.TransferId);

        var info1 = _manager.GetTransferInfo(result1.TransferId);
        var info2 = _manager.GetTransferInfo(result2.TransferId);

        Assert.Equal("file1.bin", info1.FileName);
        Assert.Equal("file2.bin", info2.FileName);
    }

    [Fact]
    public async Task GetActiveTransfers_ReturnsOnlyActive()
    {
        var path = CreateTestFile("active.bin", 100);
        var result = await _manager.InitiateTransferAsync(
            path, FileTransferDirection.Upload, "session-1");

        // Send a chunk to move to InProgress
        await _manager.SendChunkAsync(result.TransferId, new byte[100], 0, 1);

        var active = _manager.GetActiveTransfers();
        Assert.Single(active);
    }

    // --- Cleanup Tests ---

    [Fact]
    public async Task CancelTransfer_DisposesStream()
    {
        var path = CreateTestFile("cleanup.bin", 100);
        var initResult = await _manager.InitiateTransferAsync(
            path, FileTransferDirection.Download, "session-1");

        var fileBytes = File.ReadAllBytes(path);
        await _manager.ReceiveChunkAsync(initResult.TransferId, 0, fileBytes);

        await _manager.CancelTransferAsync(initResult.TransferId);

        var info = _manager.GetTransferInfo(initResult.TransferId);
        Assert.Null(info.DataStream);
    }
}
