namespace RDM.Core.Interfaces;

public interface IAudioDeviceEnumerator
{
    /// <summary>
    /// Enumerates available output (playback) devices from the audio driver layer.
    /// Can be called before audio engine initialization.
    /// Returns (SystemDeviceId, FriendlyName) pairs.
    /// </summary>
    IReadOnlyList<(string SystemId, string FriendlyName)> GetOutputDevices();

    /// <summary>
    /// Enumerates available input (recording) devices — microphones, line-in, etc.
    /// Can be called before BASS_RecordInit.
    /// Returns (SystemDeviceId, FriendlyName) pairs.
    /// </summary>
    IReadOnlyList<(string SystemId, string FriendlyName)> GetInputDevices();
}
