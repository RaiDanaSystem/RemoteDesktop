using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RemoteSupport.Shared.Clipboard;

/// <summary>
/// STA thread + native OpenClipboard so Explorer/Notepad enable Paste (CF_UNICODETEXT / CF_HDROP).
/// </summary>
internal sealed class ClipboardStaHost : IDisposable
{
    private const uint CfUnicodeText = 13;
    private const uint CfHdrop = 15;
    private const uint GmemMoveable = 0x0002;
    private const int RetryMs = 30;

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private ClipboardNativeWindow? _listener;
    private volatile bool _echoBlock;

    public event Action? ClipboardChanged;

    public ClipboardStaHost()
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "RaidanaClipboardSta"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void SetEchoBlock(bool value) => _echoBlock = value;

    public bool TryGetSnapshot(out string? text, out string[]? files)
    {
        string? t = null;
        string[]? f = null;
        var ok = false;
        Invoke(() =>
        {
            ok = ReadSnapshot(out t, out f);
        });
        text = t;
        files = f;
        return ok;
    }

    public bool SetText(string text)
    {
        var ok = false;
        Invoke(() => ok = WriteUnicodeText(text));
        return ok;
    }

    public bool SetFiles(IReadOnlyList<string> paths)
    {
        var ok = false;
        Invoke(() => ok = WriteHdrop(paths));
        return ok;
    }

    public void Invoke(Action action)
    {
        if (Thread.CurrentThread.ManagedThreadId == _thread.ManagedThreadId)
        {
            action();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        _work.Add(() =>
        {
            try { action(); }
            finally { done.Set(); }
        });
        done.Wait(TimeSpan.FromSeconds(4));
    }

    private void ThreadMain()
    {
        try
        {
            _listener = new ClipboardNativeWindow();
            _listener.Changed += () =>
            {
                if (!_echoBlock)
                    ClipboardChanged?.Invoke();
            };
        }
        catch
        {
            _listener = null;
        }

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                PumpWin32();
                if (_work.TryTake(out var job, 40, _cts.Token))
                    job();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _listener?.Dispose();
            _listener = null;
        }
    }

    private static void PumpWin32()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static bool ReadSnapshot(out string? text, out string[]? files)
    {
        text = null;
        files = null;
        if (!OpenClipboardWithRetry())
            return false;
        try
        {
            var hDrop = GetClipboardData(CfHdrop);
            if (hDrop != IntPtr.Zero)
            {
                var count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                var list = new List<string>((int)count);
                var sb = new StringBuilder(1024);
                for (uint i = 0; i < count; i++)
                {
                    sb.Clear();
                    var chars = DragQueryFile(hDrop, i, sb, (uint)sb.Capacity) + 1;
                    if (chars > sb.Capacity)
                    {
                        sb.Capacity = (int)chars + 8;
                    }
                    sb.Clear();
                    DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                    var path = sb.ToString();
                    if (File.Exists(path))
                        list.Add(path);
                }
                files = list.ToArray();
                return true;
            }

            var hText = GetClipboardData(CfUnicodeText);
            if (hText == IntPtr.Zero)
                return false;
            var locked = GlobalLock(hText);
            if (locked == IntPtr.Zero)
                return false;
            try
            {
                text = Marshal.PtrToStringUni(locked);
                return !string.IsNullOrEmpty(text);
            }
            finally
            {
                GlobalUnlock(hText);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool WriteUnicodeText(string text)
    {
        if (!OpenClipboardWithRetry())
            return false;
        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var hMem = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
            if (hMem == IntPtr.Zero)
                return false;
            var ptr = GlobalLock(hMem);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            GlobalUnlock(hMem);
            if (SetClipboardData(CfUnicodeText, hMem) == IntPtr.Zero)
            {
                GlobalFree(hMem);
                return false;
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool WriteHdrop(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(File.Exists).ToArray();
        if (existing.Length == 0)
            return false;

        var payload = BuildHdropBytes(existing);
        if (!OpenClipboardWithRetry())
            return false;
        try
        {
            EmptyClipboard();
            var hMem = GlobalAlloc(GmemMoveable, (UIntPtr)payload.Length);
            if (hMem == IntPtr.Zero)
                return false;
            var ptr = GlobalLock(hMem);
            Marshal.Copy(payload, 0, ptr, payload.Length);
            GlobalUnlock(hMem);
            if (SetClipboardData(CfHdrop, hMem) == IntPtr.Zero)
            {
                GlobalFree(hMem);
                return false;
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static byte[] BuildHdropBytes(string[] paths)
    {
        // DROPFILES + double-null-terminated Unicode paths
        var files = Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0");
        var headerSize = 20; // pFiles, pt.x, pt.y, fNC, fWide
        var buffer = new byte[headerSize + files.Length];
        BitConverter.GetBytes(headerSize).CopyTo(buffer, 0);
        BitConverter.GetBytes(1).CopyTo(buffer, 16); // fWide = TRUE
        Buffer.BlockCopy(files, 0, buffer, headerSize, files.Length);
        return buffer;
    }

    private static bool OpenClipboardWithRetry()
    {
        for (var i = 0; i < 8; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
                return true;
            Thread.Sleep(RetryMs);
        }
        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _work.CompleteAdding();
        if (!_thread.Join(1000))
        {
            // STA thread is pumping; PostQuit
        }
        _cts.Dispose();
        _work.Dispose();
    }

    private sealed class ClipboardNativeWindow : NativeWindow, IDisposable
    {
        private const int WmClipboardUpdate = 0x031D;
        public event Action? Changed;

        public ClipboardNativeWindow()
        {
            CreateHandle(new CreateParams { Caption = "RaidanaClipboardListener" });
            AddClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmClipboardUpdate)
                Changed?.Invoke();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                try { RemoveClipboardFormatListener(Handle); } catch { }
                DestroyHandle();
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }
}
