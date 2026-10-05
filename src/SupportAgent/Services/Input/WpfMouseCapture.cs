using System.Windows;
using System.Windows.Input;
using RemoteSupport.Shared.RemoteInput;
using RemoteSupport.Shared.Transport;

namespace SupportAgent.Services.Input;

public sealed class WpfMouseCapture : IAsyncDisposable
{
    private readonly CoordinateMapper _coordinateMapper;
    private readonly object _lock = new();
    private bool _isCapturing;
    private uint _sequenceNumber;
    private bool _disposed;
    private UIElement? _element;
    private Window? _window;
    private long _lastMoveTimestamp;
    private const int MouseMoveIntervalMs = 25;

    public bool IsCapturing => _isCapturing;
    public bool KeyboardEnabled { get; set; } = true;
    /// <summary>
    /// When false (AnyDesk default), only clicks/wheel/drag are sent. Local pointer stays smooth.
    /// When true, live mouse-move is injected on the customer PC.
    /// </summary>
    public bool SendPointerMoves { get; set; }

    public event Action<InputEvent>? InputCaptured;

    public WpfMouseCapture(CoordinateMapper coordinateMapper)
    {
        _coordinateMapper = coordinateMapper;
    }

    public void StartCapture(UIElement element)
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_isCapturing && ReferenceEquals(_element, element))
                return;
            if (_isCapturing && _element is not null)
                Detach(_element);
            _isCapturing = true;
            _element = element;
        }

        element.MouseMove += OnMouseMove;
        element.MouseDown += OnMouseDown;
        element.MouseUp += OnMouseUp;
        element.MouseWheel += OnMouseWheel;
        element.PreviewTextInput += OnTextInput;
        element.Focusable = true;
        if (element is FrameworkElement fe)
        {
            fe.SizeChanged += OnSizeChanged;
            fe.Loaded += OnElementLoaded;
        }

        BindWindow(element);
        element.Focus();
        Keyboard.Focus(element);
    }

    public void StopCapture(UIElement element)
    {
        lock (_lock)
        {
            if (!_isCapturing) return;
            _isCapturing = false;
        }

        Detach(element);
        _element = null;
    }

    private void Detach(UIElement element)
    {
        element.MouseMove -= OnMouseMove;
        element.MouseDown -= OnMouseDown;
        element.MouseUp -= OnMouseUp;
        element.MouseWheel -= OnMouseWheel;
        element.PreviewTextInput -= OnTextInput;
        if (element is FrameworkElement fe)
        {
            fe.SizeChanged -= OnSizeChanged;
            fe.Loaded -= OnElementLoaded;
        }
        UnbindWindow();
    }

    private void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement el)
            BindWindow(el);
    }

    private void BindWindow(UIElement element)
    {
        var window = Window.GetWindow(element);
        if (window is null || ReferenceEquals(_window, window))
            return;
        UnbindWindow();
        element.PreviewTextInput -= OnTextInput;
        _window = window;
        _window.PreviewKeyDown += OnWindowKeyDown;
        _window.PreviewKeyUp += OnWindowKeyUp;
        _window.PreviewTextInput += OnTextInput;
    }

    private void UnbindWindow()
    {
        if (_window is null) return;
        _window.PreviewKeyDown -= OnWindowKeyDown;
        _window.PreviewKeyUp -= OnWindowKeyUp;
        _window.PreviewTextInput -= OnTextInput;
        _window = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not UIElement el) return;
        var w = (int)Math.Max(1, el.RenderSize.Width);
        var h = (int)Math.Max(1, el.RenderSize.Height);
        if (_coordinateMapper.RemoteWidth > 0)
            _coordinateMapper.UpdateDimensions(_coordinateMapper.RemoteWidth, _coordinateMapper.RemoteHeight, w, h);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isCapturing || !_coordinateMapper.IsConfigured) return;

        var dragging = e.LeftButton == MouseButtonState.Pressed
            || e.RightButton == MouseButtonState.Pressed
            || e.MiddleButton == MouseButtonState.Pressed;
        if (!SendPointerMoves && !dragging)
            return;

        var now = Environment.TickCount64;
        if (now - _lastMoveTimestamp < MouseMoveIntervalMs)
            return;
        _lastMoveTimestamp = now;

        RaiseMouse(sender, e, InputEventType.MouseMove);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCapturing || !_coordinateMapper.IsConfigured) return;
        ((UIElement)sender).Focus();
        RaiseMouseButton(sender, e, KeyAction.KeyDown);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isCapturing || !_coordinateMapper.IsConfigured) return;
        RaiseMouseButton(sender, e, KeyAction.KeyUp);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isCapturing || !_coordinateMapper.IsConfigured) return;
        var pos = e.GetPosition((IInputElement)sender);
        var (remoteX, remoteY) = _coordinateMapper.MapToRemote((int)pos.X, (int)pos.Y);
        InputCaptured?.Invoke(new InputEvent
        {
            Type = InputEventType.MouseWheel,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
            MouseX = remoteX,
            MouseY = remoteY,
            WheelDelta = e.Delta
        });
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e) => HandleKey(e, KeyAction.KeyDown);

    private void OnWindowKeyUp(object sender, KeyEventArgs e) => HandleKey(e, KeyAction.KeyUp);

    private void HandleKey(KeyEventArgs e, KeyAction action)
    {
        if (e.Key == Key.Escape) return;
        if (!_isCapturing || !KeyboardEnabled) return;
        if (e.OriginalSource is System.Windows.Controls.TextBox) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.ImeProcessed or Key.DeadCharProcessed or Key.None)
            return;

        if (IsLocalClipboardShortcut(key))
            return;

        var hasCtrlOrAlt = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0;
        if (!hasCtrlOrAlt && !IsNonTextKey(key))
            return;

        e.Handled = true;
        RaiseKey(key, action);
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_isCapturing || !KeyboardEnabled) return;
        if (e.OriginalSource is System.Windows.Controls.TextBox) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return;
        if (string.IsNullOrEmpty(e.Text)) return;

        e.Handled = true;
        foreach (var ch in e.Text)
        {
            InputCaptured?.Invoke(new InputEvent
            {
                Type = InputEventType.KeyboardChar,
                TimestampTicks = DateTime.UtcNow.Ticks,
                SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
                Character = ch
            });
        }
    }

    private static bool IsLocalClipboardShortcut(Key key)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (!ctrl) return false;
        return key is Key.C or Key.V or Key.X or Key.Insert;
    }

    private static bool IsNonTextKey(Key key) => key is
        Key.Enter or Key.Return or Key.Tab or Key.Back or Key.Delete or Key.Insert
        or Key.Left or Key.Right or Key.Up or Key.Down
        or Key.Home or Key.End or Key.PageUp or Key.PageDown
        or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
        or Key.CapsLock or Key.NumLock or Key.Scroll
        or Key.Apps
        or Key.F1 or Key.F2 or Key.F3 or Key.F4 or Key.F5 or Key.F6
        or Key.F7 or Key.F8 or Key.F9 or Key.F10 or Key.F11 or Key.F12
        or Key.Space;

    private void RaiseMouse(object sender, MouseEventArgs e, InputEventType type)
    {
        var pos = e.GetPosition((IInputElement)sender);
        var (remoteX, remoteY) = _coordinateMapper.MapToRemote((int)pos.X, (int)pos.Y);
        InputCaptured?.Invoke(new InputEvent
        {
            Type = type,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
            MouseX = remoteX,
            MouseY = remoteY
        });
    }

    private void RaiseMouseButton(object sender, MouseButtonEventArgs e, KeyAction action)
    {
        var pos = e.GetPosition((IInputElement)sender);
        var (remoteX, remoteY) = _coordinateMapper.MapToRemote((int)pos.X, (int)pos.Y);
        var button = e.ChangedButton switch
        {
            System.Windows.Input.MouseButton.Left => RemoteSupport.Shared.RemoteInput.MouseButton.Left,
            System.Windows.Input.MouseButton.Right => RemoteSupport.Shared.RemoteInput.MouseButton.Right,
            System.Windows.Input.MouseButton.Middle => RemoteSupport.Shared.RemoteInput.MouseButton.Middle,
            System.Windows.Input.MouseButton.XButton1 => RemoteSupport.Shared.RemoteInput.MouseButton.X1,
            System.Windows.Input.MouseButton.XButton2 => RemoteSupport.Shared.RemoteInput.MouseButton.X2,
            _ => RemoteSupport.Shared.RemoteInput.MouseButton.Left
        };

        InputCaptured?.Invoke(new InputEvent
        {
            Type = InputEventType.MouseButton,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
            MouseX = remoteX,
            MouseY = remoteY,
            Button = button,
            ButtonAction = action
        });
    }

    private void RaiseKey(Key key, KeyAction action)
    {
        var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        InputCaptured?.Invoke(new InputEvent
        {
            Type = InputEventType.KeyboardKey,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = Interlocked.Increment(ref _sequenceNumber),
            VirtualKeyCode = vk,
            KeyAction = action
        });
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _isCapturing = false;
        if (_element is not null)
            Detach(_element);
        return ValueTask.CompletedTask;
    }
}
