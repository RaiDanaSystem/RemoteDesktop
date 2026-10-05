namespace RemoteSupport.Shared.Transport;

/// <summary>
/// Configuration for WebRTC ICE servers (STUN/TURN).
/// Matches the standard WebRTC RTCIceServer structure.
/// </summary>
public sealed class WebRtcConfiguration
{
    public List<IceServerConfig> IceServers { get; set; } = new();

    /// <summary>
    /// Default configuration using only public Google STUN.
    /// </summary>
    public static WebRtcConfiguration Default { get; } = new()
    {
        IceServers = new List<IceServerConfig>
        {
            new() { Urls = new[] { "stun:stun.l.google.com:19302" } }
        }
    };

    /// <summary>
    /// Creates a configuration with STUN and TURN servers.
    /// </summary>
    public static WebRtcConfiguration WithTurn(
        string turnUrl,
        string username,
        string credential,
        string? stunUrl = null,
        string? stunUrl2 = null)
    {
        var config = new WebRtcConfiguration
        {
            IceServers = new List<IceServerConfig>()
        };

        if (!string.IsNullOrEmpty(stunUrl))
            config.IceServers.Add(new IceServerConfig { Urls = new[] { stunUrl } });

        if (!string.IsNullOrEmpty(stunUrl2))
            config.IceServers.Add(new IceServerConfig { Urls = new[] { stunUrl2 } });

        if (string.IsNullOrEmpty(stunUrl) && string.IsNullOrEmpty(stunUrl2))
            config.IceServers.Add(new IceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } });

        config.IceServers.Add(new IceServerConfig
        {
            Urls = new[] { turnUrl },
            Username = username,
            Credential = credential
        });

        return config;
    }

    /// <summary>
    /// Creates a configuration using only TURN (relay-only mode for restrictive NATs).
    /// </summary>
    public static WebRtcConfiguration RelayOnly(
        string turnUrl,
        string username,
        string credential)
    {
        return new WebRtcConfiguration
        {
            IceServers = new List<IceServerConfig>
            {
                new()
                {
                    Urls = new[] { turnUrl },
                    Username = username,
                    Credential = credential
                }
            }
        };
    }
}

public sealed class IceServerConfig
{
    public string[] Urls { get; set; } = Array.Empty<string>();
    public string? Username { get; set; }
    public string? Credential { get; set; }
}
