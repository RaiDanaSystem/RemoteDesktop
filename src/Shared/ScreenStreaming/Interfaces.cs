namespace RemoteSupport.Shared.ScreenStreaming;

public interface IScreenCapture : IAsyncDisposable
{
    Task<ScreenCaptureResult> StartCaptureAsync(int monitorIndex = 0, CancellationToken cancellationToken = default);
    Task<FrameData?> CaptureFrameAsync(CancellationToken cancellationToken = default);
    Task StopCaptureAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken cancellationToken = default);
    Task SetMonitorAsync(int monitorIndex, CancellationToken cancellationToken = default);
    bool IsCapturing { get; }
    int CurrentMonitorIndex { get; }
    int MonitorCount { get; }
}

public interface IFrameEncoder
{
    byte[] EncodeFrame(byte[] rawPixels, int width, int height, int quality = 75);
    byte[] DecodeFrame(byte[] encodedData, out int width, out int height);
}

public interface IFrameRenderer
{
    Task RenderFrameAsync(byte[] frameData, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IScreenStreamingManager : IAsyncDisposable
{
    Task<StreamingResult> StartStreamingAsync(string sessionId, string peerId, int targetFps = 30, int quality = 75, Func<FrameData, Task>? onFrameCaptured = null, CancellationToken cancellationToken = default);
    void ApplySettings(int targetFps, int quality);
    Task StopStreamingAsync(CancellationToken cancellationToken = default);
    Task<StreamingDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default);
    bool IsStreaming { get; }
    event Action<FrameData>? FrameCaptured;
    event Action<string>? StreamingStateChanged;
}

public record ScreenCaptureResult
{
    public bool IsSuccess { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int MonitorIndex { get; init; }
    public string? ErrorMessage { get; init; }
}

public record MonitorInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsPrimary { get; init; }
    public int BoundsX { get; init; }
    public int BoundsY { get; init; }
}

public record StreamingResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
}

public class StreamingDiagnostics
{
    public bool IsStreaming { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public long BitrateBps { get; set; }
    public int DroppedFrames { get; set; }
    public int TotalFrames { get; set; }
    public int MonitorIndex { get; set; }
    public int MonitorCount { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public double CaptureTimeMs { get; set; }
    public double EncodeTimeMs { get; set; }
    public double SendTimeMs { get; set; }
}
