using EchoBridge.Models;
using NAudio.CoreAudioApi;

namespace EchoBridge.Audio;

public sealed class AudioDeviceService
{
    public IReadOnlyList<AudioDeviceInfo> GetActiveRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var result = new List<AudioDeviceInfo>();
        foreach (var device in collection)
        {
            using (device)
            {
                // Windows exposes Hands-Free as a separate render endpoint. Prefer Stereo/A2DP.
                var name = device.FriendlyName;
                if (name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Handsfree", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("AG Audio", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(new AudioDeviceInfo(device.ID, name));
            }
        }
        return result.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
