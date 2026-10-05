using System.Runtime.InteropServices;
using RemoteSupport.Shared.RemoteInput;

namespace CustomerAgent.Services.Input;

public class WindowsInputInjection : IInputInjection
{
    private bool _isEnabled;
    private int _eventsInjected;
    private int _eventsFailed;
    private DateTime? _lastEventAtUtc;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public int EventsInjected => _eventsInjected;
    public int EventsFailed => _eventsFailed;
    public DateTime? LastEventAtUtc => _lastEventAtUtc;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct MOUSEINPUT
    {
        [FieldOffset(0)] public int dx;
        [FieldOffset(4)] public int dy;
        [FieldOffset(8)] public uint mouseData;
        [FieldOffset(12)] public uint dwFlags;
        [FieldOffset(16)] public uint time;
        [FieldOffset(24)] public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct KEYBDINPUT
    {
        [FieldOffset(0)] public ushort wVk;
        [FieldOffset(2)] public ushort wScan;
        [FieldOffset(4)] public uint dwFlags;
        [FieldOffset(8)] public uint time;
        [FieldOffset(16)] public UIntPtr dwExtraInfo;
    }

    private int _streamWidth;
    private int _streamHeight;
    private int _nativeWidth;
    private int _nativeHeight;

    private int _originX;
    private int _originY;

    public void SetFramebufferSize(int streamWidth, int streamHeight, int nativeWidth, int nativeHeight, int originX = 0, int originY = 0)
    {
        _streamWidth = streamWidth;
        _streamHeight = streamHeight;
        _nativeWidth = nativeWidth;
        _nativeHeight = nativeHeight;
        _originX = originX;
        _originY = originY;
    }

    public Task<bool> InjectMouseEventAsync(
        int x, int y, MouseButton? button = null,
        KeyAction? action = null, int wheelDelta = 0,
        CancellationToken cancellationToken = default)
    {
        if (!_isEnabled)
        {
            Interlocked.Increment(ref _eventsFailed);
            return Task.FromResult(false);
        }

        try
        {
            var screenWidth = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
            var screenHeight = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
            var originX = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var originY = GetSystemMetrics(SM_YVIRTUALSCREEN);

            var physX = x;
            var physY = y;
            if (_streamWidth > 0 && _nativeWidth > 0)
                physX = (int)Math.Round(x * (_nativeWidth / (double)_streamWidth));
            if (_streamHeight > 0 && _nativeHeight > 0)
                physY = (int)Math.Round(y * (_nativeHeight / (double)_streamHeight));

            var absX = (int)(((physX + _originX - originX) * 65535.0) / screenWidth);
            var absY = (int)(((physY + _originY - originY) * 65535.0) / screenHeight);

            var inputs = new List<INPUT>();

            // Move mouse
            inputs.Add(new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dx = absX,
                        dy = absY,
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
                    }
                }
            });

            // Button press/release
            if (button.HasValue && action.HasValue)
            {
                uint downFlag = button.Value switch
                {
                    MouseButton.Left => MOUSEEVENTF_LEFTDOWN,
                    MouseButton.Right => MOUSEEVENTF_RIGHTDOWN,
                    MouseButton.Middle => MOUSEEVENTF_MIDDLEDOWN,
                    MouseButton.X1 or MouseButton.X2 => MOUSEEVENTF_XDOWN,
                    _ => 0
                };

                uint upFlag = button.Value switch
                {
                    MouseButton.Left => MOUSEEVENTF_LEFTUP,
                    MouseButton.Right => MOUSEEVENTF_RIGHTUP,
                    MouseButton.Middle => MOUSEEVENTF_MIDDLEUP,
                    MouseButton.X1 or MouseButton.X2 => MOUSEEVENTF_XUP,
                    _ => 0
                };

                uint flag = action.Value == KeyAction.KeyDown ? downFlag : upFlag;

                uint mouseData = 0;
                if (button.Value == MouseButton.X1)
                    mouseData = 0x0001;
                else if (button.Value == MouseButton.X2)
                    mouseData = 0x0002;

                inputs.Add(new INPUT
                {
                    type = INPUT_MOUSE,
                    u = new INPUTUNION
                    {
                        mi = new MOUSEINPUT { dwFlags = flag, mouseData = mouseData }
                    }
                });
            }

            // Wheel
            if (wheelDelta != 0)
            {
                inputs.Add(new INPUT
                {
                    type = INPUT_MOUSE,
                    u = new INPUTUNION
                    {
                        mi = new MOUSEINPUT
                        {
                            mouseData = (uint)wheelDelta,
                            dwFlags = MOUSEEVENTF_WHEEL
                        }
                    }
                });
            }

            var result = SendInput((uint)inputs.Count, inputs.ToArray(),
                Marshal.SizeOf<INPUT>());

            if (result > 0)
            {
                Interlocked.Increment(ref _eventsInjected);
                _lastEventAtUtc = DateTime.UtcNow;
                return Task.FromResult(true);
            }

            Interlocked.Increment(ref _eventsFailed);
            return Task.FromResult(false);
        }
        catch
        {
            Interlocked.Increment(ref _eventsFailed);
            return Task.FromResult(false);
        }
    }

    public Task<bool> InjectKeyboardEventAsync(
        ushort virtualKeyCode, KeyAction action,
        CancellationToken cancellationToken = default)
    {
        if (!_isEnabled)
        {
            Interlocked.Increment(ref _eventsFailed);
            return Task.FromResult(false);
        }

        try
        {
            uint flags = action == KeyAction.KeyUp ? KEYEVENTF_KEYUP : 0;
            if (IsExtendedKey(virtualKeyCode))
                flags |= KEYEVENTF_EXTENDEDKEY;

            var scan = (ushort)MapVirtualKey(virtualKeyCode, 0);
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = virtualKeyCode,
                        wScan = scan,
                        dwFlags = flags
                    }
                }
            };

            var result = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());

            if (result > 0)
            {
                Interlocked.Increment(ref _eventsInjected);
                _lastEventAtUtc = DateTime.UtcNow;
                return Task.FromResult(true);
            }

            Interlocked.Increment(ref _eventsFailed);
            return Task.FromResult(false);
        }
        catch
        {
            Interlocked.Increment(ref _eventsFailed);
            return Task.FromResult(false);
        }
    }

    public Task<bool> InjectCharacterAsync(char character, CancellationToken cancellationToken = default)
    {
        if (!_isEnabled)
        {
            Interlocked.Increment(ref _eventsFailed);
            return Task.FromResult(false);
        }

        try
        {
            var inputDown = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)character,
                        dwFlags = KEYEVENTF_UNICODE
                    }
                }
            };

            var inputUp = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)character,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                    }
                }
            };

            var result = SendInput(2, new[] { inputDown, inputUp },
                Marshal.SizeOf<INPUT>());

            if (result > 0)
            {
                Interlocked.Increment(ref _eventsInjected);
                _lastEventAtUtc = DateTime.UtcNow;
                return Task.FromResult(true);
            }

            Interlocked.Increment(ref _eventsFailed);
            return Task.FromResult(false);
        }
        catch
        {
            Interlocked.Increment(ref _eventsFailed);
            return Task.FromResult(false);
        }
    }

    public async Task<bool> InjectDoubleClickAsync(
        int x, int y, MouseButton button,
        CancellationToken cancellationToken = default)
    {
        await InjectMouseEventAsync(x, y, button, KeyAction.KeyDown, cancellationToken: cancellationToken);
        await Task.Delay(10, cancellationToken);
        await InjectMouseEventAsync(x, y, button, KeyAction.KeyUp, cancellationToken: cancellationToken);
        await Task.Delay(10, cancellationToken);
        await InjectMouseEventAsync(x, y, button, KeyAction.KeyDown, cancellationToken: cancellationToken);
        await Task.Delay(10, cancellationToken);
        await InjectMouseEventAsync(x, y, button, KeyAction.KeyUp, cancellationToken: cancellationToken);
        return true;
    }

    private static bool IsExtendedKey(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or
        0x25 or 0x26 or 0x27 or 0x28 or
        0x2D or 0x2E or
        0x5B or 0x5C or 0x5D or
        0xA3 or 0xA5 or
        0x6F;

    public ValueTask DisposeAsync()
    {
        _isEnabled = false;
        return ValueTask.CompletedTask;
    }
}
