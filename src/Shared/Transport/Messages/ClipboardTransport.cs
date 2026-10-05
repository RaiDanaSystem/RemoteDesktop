using System.Text.Json;
using RemoteSupport.Shared.Clipboard;

namespace RemoteSupport.Shared.Transport.Messages;

/// <summary>
/// Transport message wrapping clipboard data.
/// Prevents echo loops by including a source identifier.
/// </summary>
public sealed class ClipboardTransport
{
    public ClipboardMessageType Type { get; init; }
    public string? Text { get; init; }
    public List<ClipboardFileEntry>? Files { get; init; }
    public string SourceId { get; init; } = string.Empty;
    public long TimestampMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, s_options);

    public static ClipboardTransport? Deserialize(byte[] data)
    {
        try
        {
            return JsonSerializer.Deserialize<ClipboardTransport>(data, s_options);
        }
        catch
        {
            return null;
        }
    }

    public ClipboardMessage ToClipboardMessage()
    {
        return new ClipboardMessage
        {
            Type = Type,
            Text = Text,
            Files = Files
        };
    }
}
