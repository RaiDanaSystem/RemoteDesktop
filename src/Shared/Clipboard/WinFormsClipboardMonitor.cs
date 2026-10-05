using System.Security.Cryptography;
using System.Text;
using RemoteSupport.Shared.FileTransfer;
using Timer = System.Threading.Timer;

namespace RemoteSupport.Shared.Clipboard;

/// <summary>
/// Watches Windows clipboard on a dedicated STA thread and writes CF_UNICODETEXT / CF_HDROP natively.
/// </summary>
public sealed class WinFormsClipboardMonitor : IDisposable
{
    private const int MaxTextChars = 512 * 1024;

    private readonly ClipboardStaHost _sta = new();
    private Timer? _timer;
    private string _lastFingerprint = "";
    private bool _enabled;

    public event Action<string>? LocalTextChanged;
    public event Action<string[]>? LocalFilesChanged;

    public WinFormsClipboardMonitor()
    {
        _sta.ClipboardChanged += OnClipboardChanged;
    }

    public void Start()
    {
        _enabled = true;
        _timer ??= new Timer(_ => OnClipboardChanged(), null, TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(900));
        OnClipboardChanged();
    }

    public void Stop()
    {
        _enabled = false;
    }

    public void SetRemoteText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextChars)
            return;
        _sta.SetEchoBlock(true);
        _lastFingerprint = FingerprintText(text);
        _sta.SetText(text);
        _ = Task.Delay(800).ContinueWith(_ => _sta.SetEchoBlock(false));
    }

    public void SetRemoteFiles(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(File.Exists).ToArray();
        if (existing.Length == 0)
            return;
        _sta.SetEchoBlock(true);
        _lastFingerprint = FingerprintFiles(existing);
        _sta.SetFiles(existing);
        _ = Task.Delay(1000).ContinueWith(_ => _sta.SetEchoBlock(false));
    }

    private void OnClipboardChanged()
    {
        if (!_enabled)
            return;

        if (!_sta.TryGetSnapshot(out var text, out var files))
            return;

        if (files is { Length: > 0 })
        {
            var take = files.Take(FileTransferPump.MaxClipboardFiles).ToArray();
            var fp = FingerprintFiles(take);
            if (fp == _lastFingerprint)
                return;
            _lastFingerprint = fp;
            LocalFilesChanged?.Invoke(take);
            return;
        }

        if (string.IsNullOrEmpty(text) || text.Length > MaxTextChars)
            return;
        var textFp = FingerprintText(text);
        if (textFp == _lastFingerprint)
            return;
        _lastFingerprint = textFp;
        LocalTextChanged?.Invoke(text);
    }

    private static string FingerprintText(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "t:" + Convert.ToHexString(hash.AsSpan(0, 16));
    }

    private static string FingerprintFiles(IReadOnlyList<string> files)
    {
        var sb = new StringBuilder("f:");
        foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            long len = 0;
            try { len = new FileInfo(f).Length; } catch { }
            sb.Append(f).Append('|').Append(len).Append(';');
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        _enabled = false;
        _timer?.Dispose();
        _timer = null;
        _sta.ClipboardChanged -= OnClipboardChanged;
        _sta.Dispose();
    }
}
