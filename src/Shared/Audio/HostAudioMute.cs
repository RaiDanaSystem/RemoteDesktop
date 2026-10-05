using System.IO;
using NAudio.CoreAudioApi;

namespace RemoteSupport.Shared.Audio;

/// <summary>
/// Mutes this PC's speakers while its audio is streamed to a viewer ("listen there, not here").
/// WASAPI loopback capture is taken before the endpoint mute, so the stream keeps its sound.
/// The previous state is restored when streaming stops; a flag file lets the next app start undo a mute
/// left behind by a crash.
/// </summary>
public static class HostAudioMute
{
    private static readonly object Gate = new();
    private static string? _deviceId;
    private static bool _previouslyMuted;
    private static bool _active;

    private static string FlagPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteSupport", "host-muted.flag");

    public static bool IsActive
    {
        get { lock (Gate) return _active; }
    }

    public static void Mute()
    {
        lock (Gate)
        {
            if (_active) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _deviceId = device.ID;
                _previouslyMuted = device.AudioEndpointVolume.Mute;
                _active = true;
                if (!_previouslyMuted)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FlagPath)!);
                    File.WriteAllText(FlagPath, _deviceId);
                    device.AudioEndpointVolume.Mute = true;
                }
            }
            catch
            {
                _active = false;
                _deviceId = null;
            }
        }
    }

    public static void Restore()
    {
        lock (Gate)
        {
            if (!_active) return;
            try
            {
                if (!_previouslyMuted && _deviceId is not null)
                    SetMute(_deviceId, false);
            }
            catch { }
            TryDeleteFlag();
            _active = false;
            _deviceId = null;
        }
    }

    /// <summary>Call once at startup: undoes a mute that a crashed session never restored.</summary>
    public static void RestoreIfLeftMuted()
    {
        try
        {
            if (!File.Exists(FlagPath)) return;
            var id = File.ReadAllText(FlagPath).Trim();
            if (id.Length > 0) SetMute(id, false);
        }
        catch { }
        TryDeleteFlag();
    }

    private static void SetMute(string deviceId, bool mute)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);
        device.AudioEndpointVolume.Mute = mute;
    }

    private static void TryDeleteFlag()
    {
        try { if (File.Exists(FlagPath)) File.Delete(FlagPath); } catch { }
    }
}
