using Microsoft.Extensions.Logging;
using RDM.Shared.Enums;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Mix;

namespace RDM.Audio.Engine.Output;

/// <summary>
/// Classic BASS output backend: the per-device BASS_Init is done by <see cref="RoutingGraph"/>,
/// and a BASSmix master mixer is played directly via BASS_ChannelPlay. This is byte-for-byte
/// the engine's original output path — DirectSound-class latency through the Windows shared
/// mixer. Kept as a non-DECODE mixer on purpose; the DECODE conversion needed by WASAPI/ASIO
/// backends is introduced with those backends, not here.
/// </summary>
internal sealed class DirectSoundBackend : IOutputBackend
{
    private readonly ILogger _log;
    private int _mixerHandle;

    public DirectSoundBackend(DriverType requestedMode, ILogger log)
    {
        RequestedMode = requestedMode;
        _log          = log;
    }

    public DriverType RequestedMode { get; }

    public DriverType EffectiveMode => DriverType.DirectSound;

    public int CreateMixer(ResolvedRouting routing)
    {
        Bass.BASS_SetDevice(routing.PlayerDeviceIndex);
        _mixerHandle = BassMix.BASS_Mixer_StreamCreate(
            routing.SampleRate,
            routing.Channels,
            BASSFlag.BASS_DEFAULT);
        return _mixerHandle;
    }

    public void Start()
    {
        if (_mixerHandle != 0)
            Bass.BASS_ChannelPlay(_mixerHandle, false);
    }

    public void Stop()
    {
        if (_mixerHandle == 0) return;
        Bass.BASS_StreamFree(_mixerHandle);
        _mixerHandle = 0;
    }

    // Played (non-decode) mixer: GetLevel reads the playback buffer, does not consume decode data.
    public int GetOutputLevel() =>
        _mixerHandle != 0 ? Bass.BASS_ChannelGetLevel(_mixerHandle) : -1;
}
