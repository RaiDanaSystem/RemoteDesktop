using System.IO;
using System.Text.Json;

namespace SupportAgent.Configuration;

public sealed class ServerEndpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _filePath;

    public ServerEndpointStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RemoteSupport");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "endpoint.json");
    }

    public string? LoadOverride()
    {
        try
        {
            if (!File.Exists(_filePath))
                return null;
            var json = File.ReadAllText(_filePath);
            var dto = JsonSerializer.Deserialize<EndpointFile>(json, JsonOptions);
            return string.IsNullOrWhiteSpace(dto?.ServerUrl) ? null : dto!.ServerUrl.Trim();
        }
        catch
        {
            return null;
        }
    }

    public void SaveOverride(string serverUrl)
    {
        var dto = new EndpointFile { ServerUrl = serverUrl.Trim() };
        File.WriteAllText(_filePath, JsonSerializer.Serialize(dto, JsonOptions));
    }

    public void ClearOverride()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

    private sealed class EndpointFile
    {
        public string? ServerUrl { get; set; }
    }
}
