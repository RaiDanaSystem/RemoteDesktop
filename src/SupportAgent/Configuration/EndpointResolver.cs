namespace SupportAgent.Configuration;

public static class EndpointResolver
{
    public static bool HasBuiltInUrl => !string.IsNullOrWhiteSpace(BuiltInServer.Url);

    public static bool ShowUrlOnHome => !HasBuiltInUrl;

    public static string Resolve(string? appSettingsUrl, string? userOverride)
    {
        if (!string.IsNullOrWhiteSpace(userOverride))
            return Normalize(userOverride);

        if (HasBuiltInUrl)
            return Normalize(BuiltInServer.Url);

        if (!string.IsNullOrWhiteSpace(appSettingsUrl))
            return Normalize(appSettingsUrl);

        return "http://localhost:5096";
    }

    public static string Normalize(string url)
        => url.Trim().TrimEnd('/');

    /// <summary>
    /// Value shown in settings: user override only — never the baked-in default.
    /// </summary>
    public static string SettingsEditorValue(string? userOverride)
        => string.IsNullOrWhiteSpace(userOverride) ? string.Empty : userOverride.Trim();
}
