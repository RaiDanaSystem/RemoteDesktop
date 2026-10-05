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

        var pending = new Queue<(FrameFormat Format, byte[] Bytes, FrameData Meta)>();
        // Direct (TCP) mode can push many parts per tick; WebRTC data channels need the gentler defaults.
        var maxSendPerTick = Direct ? 256 : 8;
        var maxQueue = Direct ? 120 : 36;

        _streamingTask = Task.Run(async () =>
        {
            var frameStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var lastTrace = Environment.TickCount64;

            while (!linkedCts.Token.IsCancellationRequested && _isStreaming)
            {
                try
                {
                    frameStopwatch.Restart();
                    var sentThisTick = 0;
                    var frameInterval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(_targetFps, 1));
                    var qualityNow = _jpegQuality;

                    while (sentThisTick < maxSendPerTick && pending.Count > 0)
                    {
                        var item = pending.Dequeue();
                        var sendFrame = item.Meta;
                        sendFrame.Format = item.Format;
                        sendFrame.FrameBytes = item.Bytes;
                        FrameCaptured?.Invoke(sendFrame);
                        if (onFrameCaptured is not null)
                            await onFrameCaptured(sendFrame);
                        _totalFrames++;
                        _totalBytesSent += item.Bytes.Length;
                        sentThisTick++;
                    }

                    if (pending.Count < maxQueue)
                    {
                        var frame = await _capture.CaptureFrameAsync(linkedCts.Token);
                        if (frame is not null)
                        {
                            var encodedPackets = EncodeOutgoing(frame, qualityNow);
                            foreach (var encoded in encodedPackets)
                                pending.Enqueue((encoded.Format, encoded.Bytes, frame));

                            if (encodedPackets.Count > 4)
                            {
                                SessionTrace.Write("stream",
                                    $"encode burst packets={encodedPackets.Count} queue={pending.Count} {frame.Width}x{frame.Height} q={qualityNow}");
                            }
                        }
                        else
                        {
                            _droppedFrames++;
                        }
                    }
                    else
                    {
                        _droppedFrames++;
                    }

                    TrackMetrics(frameStopwatch.Elapsed.TotalMilliseconds, sentThisTick);

                    var now = Environment.TickCount64;
                    if (now - lastTrace >= 1000)
                    {
                        lastTrace = now;
                        SessionTrace.Stream("stream",
                            $"fps={(_fpsSamples.Count > 0 ? _fpsSamples.Average():0):0.0} queue={pending.Count} sent={_totalFrames} dropped={_droppedFrames} inFlightSend={sentThisTick}");
                    }

                    var elapsed = frameStopwatch.Elapsed;
                    if (elapsed < frameInterval)
                        await Task.Delay(frameInterval - elapsed, linkedCts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during screen capture");
                    SessionTrace.Write("stream", $"capture error: {ex.Message}");
                    _droppedFrames++;
                }
            }
        }, linkedCts.Token);

        StreamingStateChanged?.Invoke("Streaming");
        _logger.LogInformation("Screen streaming started for session {SessionId}", sessionId);

        return new StreamingResult { IsSuccess = true };
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

            _h264 ??= new OpenH264Encoder(frame.Width, frame.Height, fps: _targetFps, bitrate: H264BitrateForQuality(_jpegQuality, frame.Width, frame.Height, Direct), correctColors: CorrectColors);
            if (frame.SequenceNumber == 1 || frame.SequenceNumber % Math.Max(_targetFps, 1) == 0)
                _h264.RequestKeyframe();
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
