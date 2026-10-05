namespace SupportAgent.Configuration;

/// <summary>
/// Bake a production server into the binary. Leave empty for development
/// so the home screen can collect and persist the URL.
/// When set, the default URL is never shown on the home screen.
/// </summary>
public static class BuiltInServer
{
    public const string Url = "";
}
