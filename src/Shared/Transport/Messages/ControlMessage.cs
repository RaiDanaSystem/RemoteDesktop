using System.Text.Json;

namespace RemoteSupport.Shared.Transport.Messages;

/// <summary>
/// Control messages for session management over the DataChannel.
/// </summary>
public sealed class ControlMessage
{
    public ControlAction Action { get; init; }
    public string? Reason { get; init; }
    public int? Fps { get; init; }
    public int? Quality { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, s_options);

    public static ControlMessage? Deserialize(byte[] data)
    {
        try
        {
            return JsonSerializer.Deserialize<ControlMessage>(data, s_options);
        }
        catch
        {
            return null;
        }
    }
}

public enum ControlAction
{
    Disconnect,
    ConsentGranted,
    ConsentRevoked,
    ScreenResolutionChanged,
    Ping,
    Pong,
    ShowRemoteCursor,
    StreamSettings
}
