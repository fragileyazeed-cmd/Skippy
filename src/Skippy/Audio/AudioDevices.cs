using NAudio.CoreAudioApi;

namespace Skippy.Audio;

public sealed record AudioDevice(string Id, string Name);

public static class AudioDevices
{
    /// <summary>Setting value meaning "do not capture this source".</summary>
    public const string None = "none";

    public static List<AudioDevice> List(DataFlow flow)
    {
        var res = new List<AudioDevice>();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (d) res.Add(new AudioDevice(d.ID, d.FriendlyName));
            }
        }
        catch (Exception ex) { Core.Log.Error("Listing audio devices failed", ex); }
        return res.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// The device to use for a setting: the chosen one if it is plugged in, otherwise the Windows default
    /// for that direction (so unplugging a headset falls back to whatever Windows now plays/records on).
    /// </summary>
    public static AudioDevice? Resolve(MMDeviceEnumerator e, DataFlow flow, string? settingId)
    {
        if (settingId == None) return null;
        if (!string.IsNullOrEmpty(settingId))
        {
            try
            {
                using var d = e.GetDevice(settingId);
                if (d.State == DeviceState.Active) return new AudioDevice(d.ID, d.FriendlyName);
            }
            catch { }
        }
        try
        {
            // "Default device" in Windows Sound settings is the Console role
            if (e.TryGetDefaultAudioEndpoint(flow, Role.Console, out var def) && def != null)
                using (def) return new AudioDevice(def.ID, def.FriendlyName);
        }
        catch { }
        return null;
    }
}
