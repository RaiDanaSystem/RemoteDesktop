using System.Collections.Concurrent;
using System.Security.Cryptography;
using RemoteSupport.Shared.Transport;

namespace RemoteSupport.Shared.FileTransfer;

/// <summary>
/// Bidirectional binary file pump over the existing DataChannel.
/// Sliding-window send (no per-chunk wait) and streaming receive to disk.
/// </summary>
public sealed class FileTransferPump : IDisposable
{
    public const int MaxInFlightChunks = 10;
    public const long MaxClipboardFileBytes = 512L * 1024 * 1024;
    public const int MaxClipboardFiles = 8;

    private readonly Func<TransportMessageType, byte[], CancellationToken, Task> _send;
    private readonly ConcurrentDictionary<Guid, IncomingTransfer> _incoming = new();
    private readonly ConcurrentDictionary<Guid, ClipboardBatch> _batches = new();
    private readonly ConcurrentDictionary<Guid, bool> _clipboardTransfers = new();
    private readonly object _sendGate = new();

    public event Action<string, double, string, bool>? Progress;
    public event Action<FileTransferMessage>? OfferNeedsConsent;
    public event Action<Guid, IReadOnlyList<string>>? ClipboardBatchReady;
    public event Action<string>? Log;

    public FileTransferPump(Func<TransportMessageType, byte[], CancellationToken, Task> send)
    {
        _send = send;
    }

    public async Task SendFileAsync(
        string filePath,
        bool placeOnClipboard,
        Guid batchId,
        int batchFileCount = 1,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return;

        var info = new FileInfo(filePath);
        if (placeOnClipboard && info.Length > MaxClipboardFileBytes)
        {
            Log?.Invoke($"Skipped clipboard file (too large): {info.Name}");
            return;
        }

        var transferId = Guid.NewGuid();
        if (placeOnClipboard)
            _clipboardTransfers[transferId] = true;
        var totalChunks = Math.Max(1, (int)Math.Ceiling(info.Length / (double)FileTransferManager.ChunkSize));

        var offer = new FileTransferMessage
        {
            Type = FileTransferMessageType.FileOffer,
            TransferId = transferId,
            FileName = info.Name,
            FileSize = info.Length,
            Direction = FileTransferDirection.Upload,
            PlaceOnClipboard = placeOnClipboard,
            BatchId = batchId,
            BatchFileCount = Math.Max(1, batchFileCount)
        };

        Progress?.Invoke(info.Name, 0, placeOnClipboard ? "Clipboard…" : "Waiting…", placeOnClipboard);
        await _send(TransportMessageType.FileOffer, FileTransferMessage.Serialize(offer), cancellationToken);

        var accepted = await WaitForAcceptAsync(transferId, cancellationToken);
        if (!accepted)
        {
            Progress?.Invoke(info.Name, 0, "Rejected", placeOnClipboard);
            return;
        }

        await SendChunksAsync(filePath, transferId, totalChunks, placeOnClipboard, cancellationToken);
        var complete = new FileTransferMessage
        {
            Type = FileTransferMessageType.FileComplete,
            TransferId = transferId,
            Checksum = FileTransferMessage.ComputeFileChecksum(filePath)
        };
        await _send(TransportMessageType.FileAck, FileTransferMessage.Serialize(complete), cancellationToken);
        Progress?.Invoke(info.Name, 100, "Sent", placeOnClipboard);
    }

    public void HandleMessage(FileTransferMessage msg, bool fileTransferEnabled, bool clipboardEnabled)
    {
        switch (msg.Type)
        {
            case FileTransferMessageType.FileOffer:
                HandleOffer(msg, fileTransferEnabled, clipboardEnabled);
                break;
            case FileTransferMessageType.FileAccept:
                CompleteWait(msg.TransferId, accepted: true);
                break;
            case FileTransferMessageType.FileReject:
                CompleteWait(msg.TransferId, accepted: false);
                break;
            case FileTransferMessageType.FileChunk:
                _ = ReceiveChunkAsync(msg);
                break;
            case FileTransferMessageType.FileComplete:
                FinishIncoming(msg);
                break;
            case FileTransferMessageType.FileChunkAck:
                var name = _outgoingNames.GetValueOrDefault(msg.TransferId);
                if (!string.IsNullOrEmpty(name) && msg.TotalChunks > 0)
                {
                    var pct = (msg.ChunkIndex + 1) * 100.0 / Math.Max(1, msg.TotalChunks);
                    Progress?.Invoke(name, Math.Min(99, pct), "Sending…", _clipboardTransfers.ContainsKey(msg.TransferId));
                }
                break;
        }
    }

    public async Task RespondToOfferAsync(FileTransferMessage offer, bool accept)
    {
        var reply = new FileTransferMessage
        {
            Type = accept ? FileTransferMessageType.FileAccept : FileTransferMessageType.FileReject,
            TransferId = offer.TransferId
        };
        if (accept)
            PrepareIncoming(offer);
        await _send(TransportMessageType.FileAck, FileTransferMessage.Serialize(reply), CancellationToken.None);
    }

    public void Dispose()
    {
        foreach (var incoming in _incoming.Values)
            incoming.Dispose();
        _incoming.Clear();
        foreach (var tcs in _acceptWaiters.Values)
            tcs.TrySetResult(false);
        _acceptWaiters.Clear();
    }

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _acceptWaiters = new();
    private readonly ConcurrentDictionary<Guid, string> _outgoingNames = new();

    private async Task<bool> WaitForAcceptAsync(Guid transferId, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(60));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _acceptWaiters[transferId] = tcs;
        using var _ = linked.Token.Register(() => tcs.TrySetResult(false));
        return await tcs.Task;
    }

    private void CompleteWait(Guid transferId, bool accepted)
    {
        if (_acceptWaiters.TryRemove(transferId, out var tcs))
            tcs.TrySetResult(accepted);
    }

    private void HandleOffer(FileTransferMessage offer, bool fileTransferEnabled, bool clipboardEnabled)
    {
        if (!fileTransferEnabled)
        {
            _ = RespondToOfferAsync(offer, accept: false);
            return;
        }

        if (offer.PlaceOnClipboard)
        {
            if (!clipboardEnabled || offer.FileSize > MaxClipboardFileBytes)
            {
                _ = RespondToOfferAsync(offer, accept: false);
                return;
            }

            _ = RespondToOfferAsync(offer, accept: true);
            return;
        }

        OfferNeedsConsent?.Invoke(offer);
    }

    private void PrepareIncoming(FileTransferMessage offer)
    {
        var folder = Path.Combine(Path.GetTempPath(), "RaidanaTransfer", offer.TransferId.ToString("N"));
        Directory.CreateDirectory(folder);
        var safeName = SanitizeFileName(offer.FileName ?? "file.bin");
        var path = Path.Combine(folder, safeName);
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var incoming = new IncomingTransfer
        {
            TransferId = offer.TransferId,
            FileName = safeName,
            DestPath = path,
            FileSize = offer.FileSize,
            TotalChunks = Math.Max(1, (int)Math.Ceiling(Math.Max(1, offer.FileSize) / (double)FileTransferManager.ChunkSize)),
            PlaceOnClipboard = offer.PlaceOnClipboard,
            BatchId = offer.BatchId == Guid.Empty ? offer.TransferId : offer.BatchId,
            Stream = stream,
            Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        };
        _incoming[offer.TransferId] = incoming;

        if (incoming.PlaceOnClipboard)
        {
            var batch = _batches.GetOrAdd(incoming.BatchId, _ => new ClipboardBatch());
            lock (batch)
            {
                batch.Expected = Math.Max(batch.Expected, Math.Max(1, offer.BatchFileCount));
            }
        }
    }

    private async Task ReceiveChunkAsync(FileTransferMessage msg)
    {
        if (msg.ChunkData is null || !_incoming.TryGetValue(msg.TransferId, out var incoming))
            return;

        try
        {
            await incoming.Stream.WriteAsync(msg.ChunkData);
            incoming.Hash.AppendData(msg.ChunkData);
            incoming.ChunksReceived++;

            if (incoming.ChunksReceived % 16 == 0 || incoming.ChunksReceived >= incoming.TotalChunks)
            {
                var ack = new FileTransferMessage
                {
                    Type = FileTransferMessageType.FileChunkAck,
                    TransferId = msg.TransferId,
                    ChunkIndex = msg.ChunkIndex,
                    TotalChunks = incoming.TotalChunks
                };
                await _send(TransportMessageType.FileAck, FileTransferMessage.Serialize(ack), CancellationToken.None);
                var pct = incoming.FileSize == 0
                    ? 100
                    : incoming.Stream.Length * 100.0 / incoming.FileSize;
                Progress?.Invoke(incoming.FileName, Math.Min(99, pct), "Receiving…", incoming.PlaceOnClipboard);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Chunk write failed: {ex.Message}");
        }
    }

    private void FinishIncoming(FileTransferMessage msg)
    {
        if (!_incoming.TryRemove(msg.TransferId, out var incoming))
            return;

        try
        {
            incoming.Stream.Flush();
            incoming.Stream.Dispose();
            var local = Convert.ToHexString(incoming.Hash.GetHashAndReset());
            incoming.Hash.Dispose();

            if (!string.IsNullOrEmpty(msg.Checksum)
                && !string.Equals(local, msg.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                Progress?.Invoke(incoming.FileName, 0, "Checksum mismatch", incoming.PlaceOnClipboard);
                TryDelete(incoming.DestPath);
                return;
            }

            Progress?.Invoke(incoming.FileName, 100, "Complete", incoming.PlaceOnClipboard);

            if (incoming.PlaceOnClipboard)
            {
                if (!_batches.TryGetValue(incoming.BatchId, out var batch))
                    batch = _batches.GetOrAdd(incoming.BatchId, _ => new ClipboardBatch());

                List<string>? ready = null;
                lock (batch)
                {
                    batch.Paths.Add(incoming.DestPath);
                    if (batch.Paths.Count >= Math.Max(1, batch.Expected))
                        ready = batch.Paths.ToList();
                }

                if (ready is not null)
                {
                    _batches.TryRemove(incoming.BatchId, out _);
                    ClipboardBatchReady?.Invoke(incoming.BatchId, ready);
                }
            }
            else
            {
                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloads);
                var dest = UniquePath(Path.Combine(downloads, incoming.FileName));
                File.Copy(incoming.DestPath, dest, overwrite: false);
                Log?.Invoke($"Saved file to {dest}");
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Finish transfer failed: {ex.Message}");
            incoming.Dispose();
        }
    }

    private async Task SendChunksAsync(string filePath, Guid transferId, int totalChunks, bool isClipboard, CancellationToken cancellationToken)
    {
        _outgoingNames[transferId] = Path.GetFileName(filePath);
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[FileTransferManager.ChunkSize];
        var index = 0;
        int inFlight = 0;

        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            while (true)
            {
                lock (_sendGate)
                {
                    if (inFlight < MaxInFlightChunks)
                    {
                        inFlight++;
                        break;
                    }
                }
                await Task.Delay(4, cancellationToken);
            }

            var chunk = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunk, 0, read);
            var msg = new FileTransferMessage
            {
                Type = FileTransferMessageType.FileChunk,
                TransferId = transferId,
                ChunkIndex = index,
                TotalChunks = totalChunks,
                ChunkData = chunk
            };

            await _send(TransportMessageType.FileChunk, FileTransferMessage.Serialize(msg), cancellationToken);
            index++;

            lock (_sendGate)
            {
                inFlight = Math.Max(0, inFlight - 1);
            }

            if (index % MaxInFlightChunks == 0)
                await Task.Delay(1, cancellationToken);

            if (index % 8 == 0)
            {
                var pct = totalChunks == 0 ? 100 : index * 100.0 / totalChunks;
                Progress?.Invoke(Path.GetFileName(filePath), Math.Min(99, pct), "Sending…", isClipboard);
            }
        }

        _outgoingNames.TryRemove(transferId, out _);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file.bin" : name;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private sealed class IncomingTransfer : IDisposable
    {
        public Guid TransferId { get; init; }
        public string FileName { get; init; } = "";
        public string DestPath { get; init; } = "";
        public long FileSize { get; init; }
        public int TotalChunks { get; init; }
        public int ChunksReceived { get; set; }
        public bool PlaceOnClipboard { get; init; }
        public Guid BatchId { get; init; }
        public FileStream Stream { get; init; } = null!;
        public IncrementalHash Hash { get; init; } = null!;

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { }
            try { Hash.Dispose(); } catch { }
        }
    }

    private sealed class ClipboardBatch
    {
        public int Expected { get; set; }
        public List<string> Paths { get; } = new();
    }
}
