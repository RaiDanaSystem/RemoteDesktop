namespace RemoteSupport.Shared.RemoteInput;

public enum InputEventType : byte
{
    MouseMove = 0x01,
    MouseButton = 0x02,
    MouseWheel = 0x03,
    KeyboardKey = 0x04,
    KeyboardChar = 0x05,
    MouseDblClick = 0x06
}

public enum MouseButton : byte
{
    Left = 0,
    Right = 1,
    Middle = 2,
    X1 = 3,
    X2 = 4
}

public enum KeyAction : byte
{
    KeyDown = 0,
    KeyUp = 1
}

public class InputEvent
{
    public InputEventType Type { get; set; }
    public long TimestampTicks { get; set; }
    public uint SequenceNumber { get; set; }

    // Mouse move
    public int MouseX { get; set; }
    public int MouseY { get; set; }

    // Mouse button
    public MouseButton Button { get; set; }
    public KeyAction ButtonAction { get; set; }

    // Mouse wheel
    public int WheelDelta { get; set; }

    // Keyboard
    public ushort VirtualKeyCode { get; set; }
    public KeyAction KeyAction { get; set; }
    public char? Character { get; set; }

    public static byte[] Serialize(InputEvent evt)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)evt.Type);
        writer.Write(evt.TimestampTicks);
        writer.Write(evt.SequenceNumber);

        switch (evt.Type)
        {
            case InputEventType.MouseMove:
            case InputEventType.MouseDblClick:
                writer.Write(evt.MouseX);
                writer.Write(evt.MouseY);
                if (evt.Type == InputEventType.MouseDblClick)
                {
                    writer.Write((byte)evt.Button);
                }
                break;

            case InputEventType.MouseButton:
                writer.Write((byte)evt.Button);
                writer.Write((byte)evt.ButtonAction);
                break;

            case InputEventType.MouseWheel:
                writer.Write(evt.WheelDelta);
                break;

            case InputEventType.KeyboardKey:
                writer.Write(evt.VirtualKeyCode);
                writer.Write((byte)evt.KeyAction);
                break;

            case InputEventType.KeyboardChar:
                writer.Write(evt.Character ?? '\0');
                break;
        }

        return ms.ToArray();
    }

    public static InputEvent? Deserialize(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            var evt = new InputEvent
            {
                Type = (InputEventType)reader.ReadByte(),
                TimestampTicks = reader.ReadInt64(),
                SequenceNumber = reader.ReadUInt32()
            };

            switch (evt.Type)
            {
                case InputEventType.MouseMove:
                    evt.MouseX = reader.ReadInt32();
                    evt.MouseY = reader.ReadInt32();
                    break;

                case InputEventType.MouseDblClick:
                    evt.MouseX = reader.ReadInt32();
                    evt.MouseY = reader.ReadInt32();
                    evt.Button = (MouseButton)reader.ReadByte();
                    break;

                case InputEventType.MouseButton:
                    evt.Button = (MouseButton)reader.ReadByte();
                    evt.ButtonAction = (KeyAction)reader.ReadByte();
                    break;

                case InputEventType.MouseWheel:
                    evt.WheelDelta = reader.ReadInt32();
                    break;

                case InputEventType.KeyboardKey:
                    evt.VirtualKeyCode = reader.ReadUInt16();
                    evt.KeyAction = (KeyAction)reader.ReadByte();
                    break;

                case InputEventType.KeyboardChar:
                    evt.Character = reader.ReadChar();
                    break;
            }

            return evt;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryDeserialize(byte[] data, out InputEvent? evt)
    {
        evt = Deserialize(data);
        return evt is not null;
    }
}
