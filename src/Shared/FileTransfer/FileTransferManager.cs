using System.Collections.Concurrent;

namespace RemoteSupport.Shared.FileTransfer;

public interface IFileTransferManager
{
    Task<FileTransferInitiationResult> InitiateTransferAsync(
        string filePath, FileTransferDirection direction, string sessionId,
        Func<FileTransferMessage, Task>? onMessage = null,
        CancellationToken cancellationToken = default);

    Task<ChunkSendResult> SendChunkAsync(
        Guid transferId, byte[] data, int chunkIndex, int totalChunks,
        Func<FileTransferMessage, Task>? onMessage = null,
        CancellationToken cancellationToken = default);

    Task<ChunkReceiveResult> ReceiveChunkAsync(
        Guid transferId, int chunkIndex, byte[] data,
        CancellationToken cancellationToken = default);

    Task PauseTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
    Task ResumeTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
    Task CancelTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
    FileTransferInfo GetTransferInfo(Guid transferId);
    IReadOnlyList<FileTransferInfo> GetActiveTransfers();
}

public record FileTransferInitiationResult
{
    public bool IsSuccess { get; init; }
    public Guid TransferId { get; init; }
    public string? FileName { get; init; }
    public long FileSize { get; init; }
    public int TotalChunks { get; init; }
    public string? ErrorMessage { get; init; }
}

public record ChunkSendResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
}

public record ChunkReceiveResult
{
    public bool IsSuccess { get; init; }
    public bool IsComplete { get; init; }
    public string? Checksum { get; init; }
    public string? ErrorMessage { get; init; }
}

public class FileTransferInfo
{
    public Guid TransferId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public FileTransferDirection Direction { get; set; }
    public FileTransferStatus Status { get; set; }
    public int ChunksReceived { get; set; }
    public int TotalChunks { get; set; }
    public string? SessionId { get; set; }
    public MemoryStream? DataStream { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class FileTransferManager : IFileTransferManager
{
    private readonly ConcurrentDictionary<Guid, FileTransferInfo> _transfers = new();
    public const int ChunkSize = 32 * 1024;

    public async Task<FileTransferInitiationResult> InitiateTransferAsync(
        string filePath, FileTransferDirection direction, string sessionId,
        Func<FileTransferMessage, Task>? onMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return new FileTransferInitiationResult
            {
                IsSuccess = false,
                ErrorMessage = "File not found."
            };
        }

        var fileInfo = new FileInfo(filePath);
        var totalChunks = (int)Math.Ceiling((double)fileInfo.Length / ChunkSize);
        var transferId = Guid.NewGuid();

        var info = new FileTransferInfo
        {
            TransferId = transferId,
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            Direction = direction,
            Status = FileTransferStatus.Pending,
            TotalChunks = totalChunks,
            SessionId = sessionId,
            CreatedAtUtc = DateTime.UtcNow
        };

        _transfers[transferId] = info;

        var offer = new FileTransferMessage
        {
            Type = FileTransferMessageType.FileOffer,
            TransferId = transferId,
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            Direction = direction
        };

        if (onMessage is not null)
            await onMessage(offer);

        return new FileTransferInitiationResult
        {
            IsSuccess = true,
            TransferId = transferId,
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            TotalChunks = totalChunks
        };
    }

    public async Task<ChunkSendResult> SendChunkAsync(
        Guid transferId, byte[] data, int chunkIndex, int totalChunks,
        Func<FileTransferMessage, Task>? onMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (!_transfers.TryGetValue(transferId, out var info))
        {
            return new ChunkSendResult { IsSuccess = false, ErrorMessage = "Transfer not found." };
        }

        if (info.Status == FileTransferStatus.Paused)
        {
            return new ChunkSendResult { IsSuccess = false, ErrorMessage = "Transfer is paused." };
        }

        if (info.Status == FileTransferStatus.Cancelled)
        {
            return new ChunkSendResult { IsSuccess = false, ErrorMessage = "Transfer was cancelled." };
        }

        var chunkMsg = new FileTransferMessage
        {
            Type = FileTransferMessageType.FileChunk,
            TransferId = transferId,
            ChunkIndex = chunkIndex,
            TotalChunks = totalChunks,
            ChunkData = data
        };

        if (onMessage is not null)
            await onMessage(chunkMsg);

        info.Status = FileTransferStatus.InProgress;

        return new ChunkSendResult { IsSuccess = true };
    }

    public async Task<ChunkReceiveResult> ReceiveChunkAsync(
        Guid transferId, int chunkIndex, byte[] data,
        CancellationToken cancellationToken = default)
    {
        if (!_transfers.TryGetValue(transferId, out var info))
        {
            return new ChunkReceiveResult
            {
                IsSuccess = false,
                ErrorMessage = "Transfer not found."
            };
        }

        info.DataStream ??= new MemoryStream();
        var offset = (long)chunkIndex * ChunkSize;
        info.DataStream.Seek(offset, SeekOrigin.Begin);
        info.DataStream.Write(data, 0, data.Length);

        info.ChunksReceived++;
        info.Status = FileTransferStatus.InProgress;

        if (info.ChunksReceived >= info.TotalChunks)
        {
            info.Status = FileTransferStatus.Completed;
            info.DataStream.Seek(0, SeekOrigin.Begin);
            var allBytes = info.DataStream.ToArray();
            var checksum = FileTransferMessage.ComputeChecksum(allBytes);

            return new ChunkReceiveResult
            {
                IsSuccess = true,
                IsComplete = true,
                Checksum = checksum
            };
        }

        return new ChunkReceiveResult { IsSuccess = true, IsComplete = false };
    }

    public Task PauseTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        if (_transfers.TryGetValue(transferId, out var info))
        {
            info.Status = FileTransferStatus.Paused;
        }
        return Task.CompletedTask;
    }

    public Task ResumeTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        if (_transfers.TryGetValue(transferId, out var info) && info.Status == FileTransferStatus.Paused)
        {
            info.Status = FileTransferStatus.InProgress;
        }
        return Task.CompletedTask;
    }

    public Task CancelTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        if (_transfers.TryGetValue(transferId, out var info))
        {
            info.Status = FileTransferStatus.Cancelled;
            info.DataStream?.Dispose();
            info.DataStream = null;
        }
        return Task.CompletedTask;
    }

    public FileTransferInfo GetTransferInfo(Guid transferId)
    {
        _transfers.TryGetValue(transferId, out var info);
        return info ?? new FileTransferInfo { TransferId = transferId, Status = FileTransferStatus.Failed };
    }

    public IReadOnlyList<FileTransferInfo> GetActiveTransfers()
    {
        return _transfers.Values
            .Where(t => t.Status == FileTransferStatus.InProgress || t.Status == FileTransferStatus.Paused)
            .ToList();
    }
}
