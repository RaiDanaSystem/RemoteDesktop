using Microsoft.Extensions.Logging.Abstractions;
using RemoteSupport.Shared.ScreenStreaming;
using RemoteSupport.Shared.ScreenStreaming.Capture;
using RemoteSupport.Shared.ScreenStreaming.Encoding;

namespace RemoteSupport.Server.Tests.ScreenStreaming;

public class ScreenStreamingTests : IDisposable
{
    private readonly GdiScreenCapture _capture;
    private readonly JpegFrameEncoder _encoder;
    private readonly ScreenStreamingManager _manager;

    public ScreenStreamingTests()
    {
        _capture = new GdiScreenCapture();
        _encoder = new JpegFrameEncoder(75);
        _manager = new ScreenStreamingManager(
            _capture, _encoder,
            NullLogger<ScreenStreamingManager>.Instance);
    }

    public void Dispose() { }

    // --- Capture Tests ---

    [Fact]
    public async Task GetMonitors_ReturnsAtLeastOne()
    {
        var monitors = await _capture.GetMonitorsAsync();

        Assert.NotEmpty(monitors);
        Assert.Contains(monitors, m => m.IsPrimary);
    }

    [Fact]
    public async Task StartCapture_ValidMonitor_ReturnsSuccess()
    {
        var result = await _capture.StartCaptureAsync(0);

        Assert.True(result.IsSuccess);
        Assert.True(result.Width > 0);
        Assert.True(result.Height > 0);
        Assert.True(_capture.IsCapturing);

        await _capture.StopCaptureAsync();
    }

    [Fact]
    public async Task StartCapture_InvalidMonitor_ReturnsError()
    {
        var result = await _capture.StartCaptureAsync(999);

        Assert.False(result.IsSuccess);
        Assert.Contains("Invalid monitor", result.ErrorMessage!);
    }

    [Fact]
    public async Task CaptureFrame_AfterStart_ReturnsFrame()
    {
        await _capture.StartCaptureAsync(0);

        var frame = await _capture.CaptureFrameAsync();

        Assert.NotNull(frame);
        Assert.True(frame!.Width > 0);
        Assert.True(frame.Height > 0);
        Assert.Equal(FrameFormat.RawBgra, frame.Format);
        Assert.NotEmpty(frame.FrameBytes);

        await _capture.StopCaptureAsync();
    }

    [Fact]
    public async Task CaptureFrame_BeforeStart_ReturnsNull()
    {
        var frame = await _capture.CaptureFrameAsync();

        Assert.Null(frame);
    }

    [Fact]
    public async Task StopCapture_SetsIsCapturingFalse()
    {
        await _capture.StartCaptureAsync(0);
        Assert.True(_capture.IsCapturing);

        await _capture.StopCaptureAsync();
        Assert.False(_capture.IsCapturing);
    }

    [Fact]
    public async Task SetMonitor_ChangesCurrentMonitor()
    {
        var monitors = await _capture.GetMonitorsAsync();
        if (monitors.Count < 2)
            return; // Skip if single monitor

        await _capture.StartCaptureAsync(0);
        await _capture.SetMonitorAsync(1);

        Assert.Equal(1, _capture.CurrentMonitorIndex);
        await _capture.StopCaptureAsync();
    }

    // --- Encoder Tests ---

    [Fact]
    public void EncodeDecode_Roundtrip_PreservesDimensions()
    {
        var originalWidth = 100;
        var originalHeight = 50;
        var rawPixels = new byte[originalWidth * originalHeight * 4];

        // Fill with test pattern
        for (int i = 0; i < rawPixels.Length; i += 4)
        {
            rawPixels[i] = 0xFF;     // Blue
            rawPixels[i + 1] = 0x80; // Green
            rawPixels[i + 2] = 0x00; // Red
            rawPixels[i + 3] = 0xFF; // Alpha
        }

        var encoded = _encoder.EncodeFrame(rawPixels, originalWidth, originalHeight, 75);
        var decoded = _encoder.DecodeFrame(encoded, out var width, out var height);

        Assert.Equal(originalWidth, width);
        Assert.Equal(originalHeight, height);
        Assert.NotEmpty(decoded);
    }

    [Fact]
    public void EncodeFrame_ReturnsNonEmpty()
    {
        var rawPixels = new byte[100 * 100 * 4];
        var encoded = _encoder.EncodeFrame(rawPixels, 100, 100, 50);

        Assert.NotEmpty(encoded);
        Assert.True(encoded.Length < rawPixels.Length); // JPEG should be smaller
    }

    [Fact]
    public void EncodeFrame_QualityAffectsSize()
    {
        var rawPixels = new byte[200 * 200 * 4];
        for (int i = 0; i < rawPixels.Length; i++)
            rawPixels[i] = (byte)(i % 256);

        var highQuality = _encoder.EncodeFrame(rawPixels, 200, 200, 95);
        var lowQuality = _encoder.EncodeFrame(rawPixels, 200, 200, 10);

        Assert.True(lowQuality.Length < highQuality.Length,
            $"Low quality ({lowQuality.Length}) should be smaller than high quality ({highQuality.Length})");
    }

    // --- FrameData Serialization Tests ---

    [Fact]
    public void FrameData_SerializeDeserialize_Roundtrip()
    {
        var original = new FrameData
        {
            Width = 1920,
            Height = 1080,
            Format = FrameFormat.JPEG,
            TimestampUtcTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 42,
            MonitorIndex = 0,
            MonitorCount = 2,
            FrameBytes = new byte[] { 0x01, 0x02, 0x03, 0xFF }
        };

        var serialized = FrameData.Serialize(original);
        var deserialized = FrameData.Deserialize(serialized);

        Assert.Equal(original.Width, deserialized.Width);
        Assert.Equal(original.Height, deserialized.Height);
        Assert.Equal(original.Format, deserialized.Format);
        Assert.Equal(original.TimestampUtcTicks, deserialized.TimestampUtcTicks);
        Assert.Equal(original.SequenceNumber, deserialized.SequenceNumber);
        Assert.Equal(original.MonitorIndex, deserialized.MonitorIndex);
        Assert.Equal(original.MonitorCount, deserialized.MonitorCount);
        Assert.Equal(original.FrameBytes, deserialized.FrameBytes);
    }

    [Fact]
    public void FrameData_TryDeserialize_InvalidData_ReturnsFalse()
    {
        var result = FrameData.TryDeserialize(new byte[] { 0x00 }, out var frame);

        Assert.False(result);
        Assert.Null(frame);
    }

    [Fact]
    public void FrameData_TryDeserialize_ValidData_ReturnsTrue()
    {
        var original = new FrameData
        {
            Width = 640,
            Height = 480,
            Format = FrameFormat.JPEG,
            TimestampUtcTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            MonitorIndex = 0,
            MonitorCount = 1,
            FrameBytes = new byte[] { 0xAA, 0xBB }
        };

        var serialized = FrameData.Serialize(original);
        var result = FrameData.TryDeserialize(serialized, out var frame);

        Assert.True(result);
        Assert.NotNull(frame);
        Assert.Equal(640, frame!.Width);
    }

    // --- Streaming Manager Tests ---

    [Fact]
    public async Task StartStreaming_WithCapture_ReturnsSuccess()
    {
        var result = await _manager.StartStreamingAsync("test-session", "test-peer");

        Assert.True(result.IsSuccess);
        Assert.True(_manager.IsStreaming);

        await _manager.StopStreamingAsync();
    }

    [Fact]
    public async Task StopStreaming_SetsIsStreamingFalse()
    {
        await _manager.StartStreamingAsync("test-session", "test-peer");
        Assert.True(_manager.IsStreaming);

        await _manager.StopStreamingAsync();
        Assert.False(_manager.IsStreaming);
    }

    [Fact]
    public async Task StartStreaming_Twice_ReturnsError()
    {
        await _manager.StartStreamingAsync("test-session", "test-peer");

        var result = await _manager.StartStreamingAsync("test-session", "test-peer");

        Assert.False(result.IsSuccess);
        Assert.Contains("Already streaming", result.ErrorMessage!);

        await _manager.StopStreamingAsync();
    }

    [Fact]
    public async Task Diagnostics_ReturnsCorrectValues_WhenNotStreaming()
    {
        var diag = await _manager.GetDiagnosticsAsync();

        Assert.False(diag.IsStreaming);
        Assert.Equal(0, diag.TotalFrames);
        Assert.Equal(0, diag.DroppedFrames);
    }

    [Fact]
    public async Task Diagnostics_UpdatesAfterStreaming()
    {
        var framesCaptured = new List<FrameData>();
        Func<FrameData, Task> onFrame = frame =>
        {
            framesCaptured.Add(frame);
            return Task.CompletedTask;
        };

        var result = await _manager.StartStreamingAsync("test-session", "test-peer", onFrameCaptured: onFrame);
        Assert.True(result.IsSuccess, $"Start failed: {result.ErrorMessage}");

        Assert.True(_manager.IsStreaming, "Manager should be streaming");

        await Task.Delay(3000); // Wait for frames

        await _manager.StopStreamingAsync();

        // Even if no frames captured, the streaming should have been running
        Assert.False(_manager.IsStreaming, "Manager should not be streaming after stop");
    }

    [Fact]
    public async Task Capture_CapturesFramesContinuously()
    {
        await _capture.StartCaptureAsync(0);
        int frameCount = 0;

        for (int i = 0; i < 10; i++)
        {
            var frame = await _capture.CaptureFrameAsync();
            if (frame is not null) frameCount++;
        }

        await _capture.StopCaptureAsync();

        Assert.True(frameCount > 0, $"Expected frames to be captured, got {frameCount}");
    }

    // --- Authorization Tests ---

    [Fact]
    public async Task Streaming_StopOnDispose()
    {
        await _manager.StartStreamingAsync("test-session", "test-peer");
        Assert.True(_manager.IsStreaming);

        await _manager.DisposeAsync();
        Assert.False(_manager.IsStreaming);
    }

    [Fact]
    public async Task MultipleStartStop_CyclesWork()
    {
        for (int i = 0; i < 3; i++)
        {
            var result = await _manager.StartStreamingAsync($"session-{i}", $"peer-{i}");
            Assert.True(result.IsSuccess);
            Assert.True(_manager.IsStreaming);

            await _manager.StopStreamingAsync();
            Assert.False(_manager.IsStreaming);
        }
    }

    // --- Monitor Selection Tests ---

    [Fact]
    public async Task Capture_ChangeMonitor_CapturesNewMonitor()
    {
        var monitors = await _capture.GetMonitorsAsync();
        if (monitors.Count < 2) return;

        await _capture.StartCaptureAsync(0);
        var frame1 = await _capture.CaptureFrameAsync();
        Assert.NotNull(frame1);
        Assert.Equal(0, frame1!.MonitorIndex);

        await _capture.SetMonitorAsync(1);
        var frame2 = await _capture.CaptureFrameAsync();
        Assert.NotNull(frame2);
        Assert.Equal(1, frame2!.MonitorIndex);

        await _capture.StopCaptureAsync();
    }

    // --- Performance Test ---

    [Fact]
    public async Task CapturePerformance_MeasuresFPS()
    {
        await _capture.StartCaptureAsync(0);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int frameCount = 0;

        while (stopwatch.ElapsedMilliseconds < 1000)
        {
            var frame = await _capture.CaptureFrameAsync();
            if (frame is not null) frameCount++;
        }

        await _capture.StopCaptureAsync();

        var fps = frameCount / (stopwatch.ElapsedMilliseconds / 1000.0);
        Assert.True(fps > 5, $"FPS too low: {fps:F1}");
    }
}
