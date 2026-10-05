namespace RemoteSupport.Shared.Diagnostics;

/// <summary>
/// Lightweight session diagnostics written next to the app exe (session-trace.log).
/// </summary>
public static class SessionTrace
{
    private static readonly object Gate = new();
    private static string? _path;
    private static long _lastMouseLogMs;
    private static long _lastStreamLogMs;

    public static string LogPath => _path ??= Path.Combine(AppContext.BaseDirectory, "session-trace.log");

    public static void Write(string role, string message)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{role}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // tracing must never break the session
        }
    }

    public static void Mouse(string role, string message, int intervalMs = 400)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastMouseLogMs) < intervalMs)
            return;
        Interlocked.Exchange(ref _lastMouseLogMs, now);
        Write(role, message);
    }

    public static void Stream(string role, string message, int intervalMs = 1000)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastStreamLogMs) < intervalMs)
            return;
        Interlocked.Exchange(ref _lastStreamLogMs, now);
        Write(role, message);
    }
}
