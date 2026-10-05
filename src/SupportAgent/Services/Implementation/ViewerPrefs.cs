using System.IO;
using System.Text.Json;

namespace SupportAgent.Services.Implementation;

/// <summary>Small persisted viewer preferences (last choices survive restarts).</summary>
public static class ViewerPrefs
{
    private sealed class Data
    {
        public int AudioMode { get; set; }
    }

    private static string PathFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteSupport", "viewer.json");

    private static Data Load()
    {
        try
        {
            if (File.Exists(PathFile))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(PathFile)) ?? new Data();
        }
        catch { }
        return new Data();
    }

    /// <summary>0 = off, 1 = play here, 2 = play here and mute the remote PC's speakers.</summary>
    public static int AudioMode
    {
        get => Math.Clamp(Load().AudioMode, 0, 2);
        set
        {
            try
            {
                var d = Load();
                d.AudioMode = Math.Clamp(value, 0, 2);
                Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
                File.WriteAllText(PathFile, JsonSerializer.Serialize(d));
            }
            catch { }
        }
    }
}
