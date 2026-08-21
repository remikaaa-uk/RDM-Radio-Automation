using RDM.Core.Interfaces;
using Un4seen.Bass;

namespace RDM.Audio.Engine;

/// <summary>
/// Enumerates available output devices using BASS_GetDeviceInfos().
/// Works before BASS_Init — safe to call from Settings UI at any time.
/// </summary>
public sealed class BassDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<(string SystemId, string FriendlyName)> GetOutputDevices()
    {
        var infos   = Bass.BASS_GetDeviceInfos();
        var result  = new List<(string, string)>();

        for (int i = 0; i < infos.Length; i++)
        {
            var info = infos[i];
            if (info is null || string.IsNullOrWhiteSpace(info.name)) continue;

            // Index 0 is always the "No Sound" virtual device in BASS.
            // Real devices have a non-empty driver string.
            if (i == 0 || string.IsNullOrWhiteSpace(info.driver)) continue;

            var systemId = string.IsNullOrEmpty(info.id) ? $"bass-idx-{i}" : info.id;
            result.Add((systemId, info.name));
        }

        return result;
    }

    public IReadOnlyList<(string SystemId, string FriendlyName)> GetInputDevices()
    {
        var infos  = Bass.BASS_RecordGetDeviceInfos();
        var result = new List<(string, string)>();
        if (infos is null) return result;

        for (int i = 0; i < infos.Length; i++)
        {
            var info = infos[i];
            if (info is null || string.IsNullOrWhiteSpace(info.name)) continue;
            if (string.IsNullOrWhiteSpace(info.driver)) continue;

            var systemId = string.IsNullOrEmpty(info.id) ? $"bass-rec-{i}" : info.id;
            result.Add((systemId, info.name));
        }

        return result;
    }
}
