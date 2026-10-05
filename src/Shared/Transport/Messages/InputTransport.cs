using System.Text.Json;
using RemoteSupport.Shared.RemoteInput;

namespace RemoteSupport.Shared.Transport.Messages;

/// <summary>
/// Transport message wrapping a remote input event.
/// Sent from SupportAgent to CustomerAgent over the DataChannel.
/// </summary>
public sealed class InputTransport
{
    public InputEventType Type { get; init; }
    public long TimestampMs { get; init; }
    public int MouseX { get; init; }
    public int MouseY { get; init; }
    public MouseButton Button { get; init; }
    public KeyAction Action { get; init; }
    public int WheelDelta { get; init; }
    public ushort VirtualKeyCode { get; init; }
    public char? Character { get; init; }

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, s_options);

    public static InputTransport? Deserialize(byte[] data)
    {
        try
        {
            return JsonSerializer.Deserialize<InputTransport>(data, s_options);
        }
        catch
        {
            return null;
        }
    }

    public InputEvent ToInputEvent()
    {
        return new InputEvent
        {
            Type = Type,
            TimestampTicks = new DateTimeOffset(TimestampMs, TimeSpan.Zero).Ticks,
            MouseX = MouseX,
            MouseY = MouseY,
            Button = Button,
            ButtonAction = Action,
            WheelDelta = WheelDelta,
            VirtualKeyCode = VirtualKeyCode,
            KeyAction = Action,
            Character = Character
        };
    }

    public static InputTransport FromInputEvent(InputEvent evt)
    {
        return new InputTransport
        {
            Type = evt.Type,
            TimestampMs = new DateTimeOffset(evt.TimestampTicks, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            MouseX = evt.MouseX,
            MouseY = evt.MouseY,
            Button = evt.Button,
            Action = evt.Type is InputEventType.KeyboardKey or InputEventType.KeyboardChar
                ? evt.KeyAction
                : evt.ButtonAction,
            WheelDelta = evt.WheelDelta,
            VirtualKeyCode = evt.VirtualKeyCode,
            Character = evt.Character
        };
    }
}
