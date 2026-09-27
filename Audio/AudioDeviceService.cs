using EchoBridge.Models;
using EchoBridge.Services;
using NAudio.CoreAudioApi;
using System.Text;

namespace EchoBridge.Audio;

public sealed class AudioDeviceService
{
    public IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var collection = enumerator.EnumerateAudioEndPoints(
            DataFlow.Render, DeviceState.Active | DeviceState.Unplugged);
        var result = new List<AudioDeviceInfo>();
        var diagnostics = new StringBuilder($"[AudioDeviceService] Render endpoints found: {collection.Count}");
        foreach (var device in collection)
        {
            using (device)
            {
                try
                {
                    var name = device.FriendlyName;
                    var id = device.ID;
                    var state = device.State;
                    var flow = device.DataFlow;
                    result.Add(new AudioDeviceInfo(id, name));
                    diagnostics.Append($"{Environment.NewLine}Name: {name}{Environment.NewLine}State: {state}")
                        .Append($"{Environment.NewLine}Flow: {flow}{Environment.NewLine}ID: {id}");
                }
                catch (Exception ex)
                {
                    LoggingService.Write("[AudioDeviceService] Endpoint changed during enumeration", ex);
                }
            }
        }
        LoggingService.Write(diagnostics.ToString());
        return result.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
