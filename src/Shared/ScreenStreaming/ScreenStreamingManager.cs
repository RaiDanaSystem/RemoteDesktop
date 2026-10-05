using System.Linq;
using Microsoft.Extensions.Logging;
using RemoteSupport.Shared.Diagnostics;
using RemoteSupport.Shared.ScreenStreaming.Encoding;
using RemoteSupport.Shared.ScreenStreaming.Tiling;
using RemoteSupport.Shared.ScreenStreaming.Video;

namespace RemoteSupport.Shared.ScreenStreaming;

public class ScreenStreamingManager : IScreenStreamingManager
{
    private readonly IScreenCapture _capture;
    private readonly IFrameEncoder _encoder;
    private readonly ILogger<ScreenStreamingManager> _logger;
    private readonly TileStreamingOptions _tileOptions;
    private readonly DirtyTileEncoder _tileEncoder;
    private OpenH264Encoder? _h264;
    private bool _h264Unavailable;
    private int _targetFps = 20;
    private int _jpegQuality = 55;
    private int _rebuildEncoder;
    private long _lastKeyframeTick;
    private CancellationTokenSource? _streamingCts;
    private Task? _streamingTask;
    private bool _isStreaming;
    private uint _totalFrames;
    private int _droppedFrames;
    private DateTime? _startedAtUtc;
    private readonly Queue<double> _fpsSamples = new();
    private readonly Queue<double> _bitrateSamples = new();
    private long _totalBytesSent;

    public bool IsStreaming => _isStreaming;

    /// <summary>True when frames go over a server-less LAN TCP channel (bigger send bursts, higher bitrates).</summary>
    public bool Direct { get; set; }

    /// <summary>Viewer asked for JPEG tiles (compatibility mode) instead of H.264.</summary>
    public bool PreferTiles { get; set; }

    /// <summary>Encode H.264 with correct colors (for hardware-decoding viewers such as Android).</summary>
    public bool CorrectColors { get; set; }

    /// <summary>Use the multi-threaded OpenH264 setup (viewers that ask for it; big win at 1440p/4K).</summary>
    public bool FastEncoder { get; set; }
    public event Action<FrameData>? FrameCaptured;
    public event Action<string>? StreamingStateChanged;

    public ScreenStreamingManager(
        IScreenCapture capture,
        IFrameEncoder encoder,
        ILogger<ScreenStreamingManager> logger,
        TileStreamingOptions? tileOptions = null)
    {
        _capture = capture;
        _encoder = encoder;
        _logger = logger;
        _tileOptions = tileOptions ?? new TileStreamingOptions();
        _tileEncoder = new DirtyTileEncoder(encoder as JpegFrameEncoder ?? new JpegFrameEncoder(_tileOptions.JpegQuality));
    }

    public async Task<StreamingResult> StartStreamingAsync(
        string sessionId, string peerId,
        int targetFps = 30, int quality = 75,
        Func<FrameData, Task>? onFrameCaptured = null,
        CancellationToken cancellationToken = default)
    {
        if (_isStreaming)
        {
            await StopStreamingAsync(cancellationToken);
        }

        var monitors = await _capture.GetMonitorsAsync(cancellationToken);
        if (monitors.Count == 0)
        {
            return new StreamingResult { IsSuccess = false, ErrorMessage = "No monitors found." };
        }

        var captureResult = await _capture.StartCaptureAsync(0, cancellationToken);
        if (!captureResult.IsSuccess)
        {
            return new StreamingResult { IsSuccess = false, ErrorMessage = captureResult.ErrorMessage };
        }

        _isStreaming = true;
        _totalFrames = 0;
        _droppedFrames = 0;
        _totalBytesSent = 0;
        _tileEncoder.Reset();
        _h264?.Dispose();
        _h264 = null;
        _h264Unavailable = false;
        _tileOptions.JpegQuality = quality;
        _jpegQuality = Math.Clamp(quality, 20, 80);
        _targetFps = Math.Clamp(targetFps, 5, 60);
        _startedAtUtc = DateTime.UtcNow;
        _streamingCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _streamingCts.Token, cancellationToken);

        // Three-stage pipeline so capture, encode and network send overlap:
        //   capture (paced by fps) -> latest-frame slot -> encode -> send queue -> network
        // Slow encoders just drop captured frames (newest wins), which keeps latency low.
        var pending = new System.Collections.Concurrent.ConcurrentQueue<(FrameFormat Format, byte[] Bytes, FrameData Meta)>();
        // Direct (TCP) mode can queue many parts; WebRTC data channels need the gentler limit.
        var maxQueue = Direct ? 120 : 36;
        var sendSignal = new SemaphoreSlim(0);
        var encodeSignal = new SemaphoreSlim(0);
        FrameData? latest = null;
        var token = linkedCts.Token;

        var captureTask = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ulong lastHash = 0;
            var staticFrames = 0;
            var lastSignalTick = 0L;
            while (!token.IsCancellationRequested && _isStreaming)
            {
                try
                {
                    sw.Restart();
                    var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(_targetFps, 1));

                    // Don't capture faster than the encoder can consume: a frame nobody encodes is wasted CPU/heat.
                    if (Volatile.Read(ref latest) is not null)
                    {
                        await Task.Delay(2, token);
                        continue;
                    }

                    var frame = await _capture.CaptureFrameAsync(token);
                    if (frame is not null)
                    {
                        // Unchanged screen: skip encoding (and poll slower) but still send a heartbeat frame
                        // twice a second so keyframes/recovery keep working.
                        var hash = frame.Format == FrameFormat.RawBgra ? QuickHash(frame) : 0UL;
                        var now = Environment.TickCount64;
                        // H.264 decoders hold back output until more input arrives, so keep a steady trickle (~15 fps) on idle screens.
                        var heartbeatMs = PreferTiles ? 500 : 66;
                        var same = hash != 0 && hash == lastHash && now - lastSignalTick < heartbeatMs;
                        lastHash = hash;
                        if (same)
                        {
                            staticFrames++;
                            (_capture as Capture.GdiScreenCapture)?.Recycle(frame.FrameBytes);
                        }
                        else
                        {
                            staticFrames = 0;
                            lastSignalTick = now;
                            if (Interlocked.Exchange(ref latest, frame) is not null)
                                _droppedFrames++;
                            encodeSignal.Release();
                        }
                    }
                    else
                    {
                        _droppedFrames++;
                    }

                    // Idle screen: poll at ~7 fps instead of the full rate until something changes.
                    {
                        var idle = TimeSpan.FromMilliseconds(PreferTiles ? 140 : 66);
                        if (staticFrames >= 8 && interval < idle) interval = idle;
                    }

                    var elapsed = sw.Elapsed;
                    if (elapsed < interval)
                        await Task.Delay(interval - elapsed, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during screen capture");
                    SessionTrace.Write("stream", $"capture error: {ex.Message}");
                    _droppedFrames++;
                }
            }
        }, token);

        var encodeTask = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lastTrace = Environment.TickCount64;
            while (!token.IsCancellationRequested && _isStreaming)
            {
                try
                {
                    await encodeSignal.WaitAsync(token);
                    var frame = Interlocked.Exchange(ref latest, null);
                    if (frame is null) continue;
                    if (pending.Count >= maxQueue)
                    {
                        _droppedFrames++;
                        (_capture as Capture.GdiScreenCapture)?.Recycle(frame.FrameBytes);
                        continue;
                    }

                    sw.Restart();
                    var encodedPackets = EncodeOutgoing(frame, _jpegQuality);
                    if (frame.Format == FrameFormat.RawBgra)
                        (_capture as Capture.GdiScreenCapture)?.Recycle(frame.FrameBytes);
                    foreach (var encoded in encodedPackets)
                    {
                        pending.Enqueue((encoded.Format, encoded.Bytes, frame));
                        sendSignal.Release();
                    }
                    TrackMetrics(sw.Elapsed.TotalMilliseconds, 0);

                    if (encodedPackets.Count > 4)
                    {
                        SessionTrace.Write("stream",
                            $"encode burst packets={encodedPackets.Count} queue={pending.Count} {frame.Width}x{frame.Height} q={_jpegQuality}");
                    }

                    var now = Environment.TickCount64;
                    if (now - lastTrace >= 1000)
                    {
                        lastTrace = now;
                        SessionTrace.Stream("stream",
                            $"encode={sw.Elapsed.TotalMilliseconds:0}ms queue={pending.Count} sent={_totalFrames} dropped={_droppedFrames}");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during frame encode");
                    SessionTrace.Write("stream", $"encode error: {ex.Message}");
                    _droppedFrames++;
                }
            }
        }, token);

        var sendTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && _isStreaming)
            {
                try
                {
                    await sendSignal.WaitAsync(token);
                    if (!pending.TryDequeue(out var item)) continue;
                    var sendFrame = item.Meta;
                    sendFrame.Format = item.Format;
                    sendFrame.FrameBytes = item.Bytes;
                    FrameCaptured?.Invoke(sendFrame);
                    if (onFrameCaptured is not null)
                        await onFrameCaptured(sendFrame);
                    _totalFrames++;
                    _totalBytesSent += item.Bytes.Length;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during frame send");
                }
            }
        }, token);

        _streamingTask = Task.WhenAll(captureTask, encodeTask, sendTask);

        StreamingStateChanged?.Invoke("Streaming");
        _logger.LogInformation("Screen streaming started for session {SessionId}", sessionId);

        return new StreamingResult { IsSuccess = true };
    }

    /// <summary>
    /// Cheap change detector: hashes every 8th row of the frame (a text caret or glyph always spans several rows,
    /// so edits are still caught). Returns non-zero.
    /// </summary>
    private static ulong QuickHash(FrameData frame)
    {
        var bytes = frame.FrameBytes;
        var stride = frame.Width * 4;
        if (stride <= 0 || bytes.Length < stride) return 0;
        ulong h = 1469598103934665603UL;
        for (var y = 0; y + 1 <= frame.Height; y += 8)
        {
            var offset = y * stride;
            if (offset + stride > bytes.Length) break;
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(bytes.AsSpan(offset, stride));
            for (var i = 0; i < words.Length; i++)
                h = (h ^ words[i]) * 1099511628211UL;
        }
        return h | 1UL;
    }

    public void ApplySettings(int targetFps, int quality)
    {
        _targetFps = Math.Clamp(targetFps, 5, 60);
        _jpegQuality = Math.Clamp(quality, 20, 80);
        _tileOptions.JpegQuality = _jpegQuality;
        Interlocked.Exchange(ref _rebuildEncoder, 1);
        _logger.LogInformation("Stream settings applied: fps={Fps} quality={Quality}", _targetFps, _jpegQuality);
    }

    public async Task StopStreamingAsync(CancellationToken cancellationToken = default)
    {
        if (!_isStreaming) return;

        _isStreaming = false;
        _streamingCts?.Cancel();

        if (_streamingTask is not null)
        {
            try
            {
                await Task.WhenAny(_streamingTask, Task.Delay(3000, cancellationToken));
            }
            catch { }
        }

        await _capture.StopCaptureAsync(cancellationToken);
        _h264?.Dispose();
        _h264 = null;
        StreamingStateChanged?.Invoke("Stopped");
        _logger.LogInformation("Screen streaming stopped. Total frames: {Total}, Dropped: {Dropped}",
            _totalFrames, _droppedFrames);
    }

    public Task<StreamingDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var fps = _fpsSamples.Count > 0 ? _fpsSamples.Average() : 0;
        var bitrate = _bitrateSamples.Count > 0 ? _bitrateSamples.Average() : 0;

        return Task.FromResult(new StreamingDiagnostics
        {
            IsStreaming = _isStreaming,
            Width = _capture.IsCapturing ? 0 : 0,
            Height = _capture.IsCapturing ? 0 : 0,
            Fps = Math.Round(fps, 1),
            BitrateBps = (long)bitrate,
            DroppedFrames = _droppedFrames,
            TotalFrames = (int)_totalFrames,
            MonitorIndex = _capture.CurrentMonitorIndex,
            MonitorCount = _capture.MonitorCount,
            StartedAtUtc = _startedAtUtc
        });
    }

    private List<(FrameFormat Format, byte[] Bytes)> EncodeOutgoing(FrameData frame, int quality)
    {
        if (frame.Format == FrameFormat.JPEG || frame.Format == FrameFormat.JpegTiles)
            return [(frame.Format, frame.FrameBytes)];

        if (frame.Format == FrameFormat.RawBgra)
        {
            var h264Packets = TryEncodeH264(frame);
            if (h264Packets is not null)
                return h264Packets;

            _tileOptions.JpegQuality = quality;
            var tiled = _tileEncoder.Encode(frame.FrameBytes, frame.Width, frame.Height, frame.SequenceNumber, _tileOptions);
            if (tiled is null)
                return [];
            return tiled.Value.Packets.Select(p => (FrameFormat.JpegTiles, p)).ToList();
        }

        var encodedBytes = _encoder.EncodeFrame(frame.FrameBytes, frame.Width, frame.Height, quality);
        return [(FrameFormat.JPEG, encodedBytes)];
    }

    private List<(FrameFormat Format, byte[] Bytes)>? TryEncodeH264(FrameData frame)
    {
        if (_h264Unavailable || PreferTiles)
            return null;

        try
        {
            if (Interlocked.Exchange(ref _rebuildEncoder, 0) == 1)
            {
                try { _h264?.Dispose(); } catch { }
                _h264 = null;
            }

            _h264 ??= new OpenH264Encoder(frame.Width, frame.Height, fps: _targetFps, bitrate: H264BitrateForQuality(_jpegQuality, frame.Width, frame.Height, Direct), correctColors: CorrectColors, fast: FastEncoder);
            var tick = Environment.TickCount64;
            if (frame.SequenceNumber <= 1 || tick - _lastKeyframeTick >= 1000)
            {
                _lastKeyframeTick = tick;
                _h264.RequestKeyframe();
            }
            var annexB = _h264.EncodeBgra(frame.FrameBytes, frame.Width, frame.Height, out var keyframe);
            if (annexB is null || annexB.Length == 0)
                return [];

            if (frame.SequenceNumber <= 5 || frame.SequenceNumber % 30 == 0)
            {
                SessionTrace.Stream("stream",
                    $"h264 seq={frame.SequenceNumber} {frame.Width}x{frame.Height} bytes={annexB.Length} key={keyframe}");
            }
            return H264AccessUnitPacket
                .Split(frame.Width, frame.Height, frame.SequenceNumber, keyframe, annexB)
                .Select(p => (FrameFormat.H264, p))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "H.264 encode failed; falling back to JPEG tiles");
            SessionTrace.Write("stream", $"h264 unavailable: {ex.Message}");
            _h264Unavailable = true;
            _h264?.Dispose();
            _h264 = null;
            return null;
        }
    }

    private static int H264BitrateForQuality(int quality, int width, int height, bool direct)
    {
        var q = Math.Clamp(quality, 20, 80);
        var perHd = 250_000 + (int)((q - 20) / 60.0 * 4_750_000);
        // Scale with pixel count relative to 1080p so 1440p/4K keep the same visual quality.
        var area = Math.Clamp((double)width * height / (1920.0 * 1080.0), 0.25, 4.5);
        var bitrate = perHd * area;
        // On a LAN there is plenty of bandwidth: give high quality settings extra headroom.
        if (direct && q >= 60) bitrate *= 1.6;
        return (int)Math.Min(bitrate, 80_000_000);
    }

    private void TrackMetrics(double frameTimeMs, int frameBytes)
    {
        _fpsSamples.Enqueue(1000.0 / Math.Max(frameTimeMs, 1));
        if (_fpsSamples.Count > 30) _fpsSamples.Dequeue();

        _bitrateSamples.Enqueue(frameBytes * 8 * (1000.0 / Math.Max(frameTimeMs, 1)));
        if (_bitrateSamples.Count > 30) _bitrateSamples.Dequeue();
    }

    public async ValueTask DisposeAsync()
    {
        await StopStreamingAsync();
        _streamingCts?.Dispose();
    }
}
