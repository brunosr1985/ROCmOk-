using NAudio.CoreAudioApi;

namespace RocmAudioSplitter.Core.Audio;

public sealed record AudioEndpointInfo(
    string Id,
    string FriendlyName,
    DataFlow Flow,
    bool IsDefault,
    bool IsVirtualCable);

public static class WasapiDeviceEnumerator
{
    public static IReadOnlyList<AudioEndpointInfo> GetPlaybackDevices()
    {
        return EnumerateDevices(DataFlow.Render);
    }

    public static IReadOnlyList<AudioEndpointInfo> GetCaptureDevices()
    {
        return EnumerateDevices(DataFlow.Capture);
    }

    public static AudioEndpointInfo? FindVirtualAudioCableOutput()
    {
        return GetPlaybackDevices().FirstOrDefault(d => d.IsVirtualCable);
    }

    private static IReadOnlyList<AudioEndpointInfo> EnumerateDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = def.ID;
        }
        catch
        {
            // Nenhum dispositivo padrão ativo
        }

        var list = new List<AudioEndpointInfo>();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                string name = device.FriendlyName;
                bool isVirtual = name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase);

                list.Add(new AudioEndpointInfo(
                    Id: device.ID,
                    FriendlyName: name,
                    Flow: flow,
                    IsDefault: device.ID == defaultId,
                    IsVirtualCable: isVirtual));
            }
        }

        return list;
    }
}