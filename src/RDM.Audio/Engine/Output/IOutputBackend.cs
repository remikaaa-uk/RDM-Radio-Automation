using RDM.Shared.Enums;

namespace RDM.Audio.Engine.Output;

/// <summary>
/// Output backend abstraction: encapsulates how the master mix is rendered to the
/// selected audio driver. Only DirectSound exists today; WASAPI (shared/exclusive)
/// and ASIO backends slot into this same seam in later milestones.
///
/// Lifecycle mirrors the engine's original inline sequence EXACTLY so behaviour is
/// unchanged for DirectSound:
///   CreateMixer  →  [engine registers the stall sync on the returned handle]  →  Start
///   … running …
///   Stop
/// </summary>
internal interface IOutputBackend
{
    /// <summary>Driver mode the user selected in settings.</summary>
    DriverType RequestedMode { get; }

    /// <summary>
    /// Driver mode that actually runs. May differ from <see cref="RequestedMode"/> when
    /// the requested mode is not yet implemented and the backend falls back.
    /// </summary>
    DriverType EffectiveMode { get; }

    /// <summary>
    /// Creates the BASSmix master mixer on the routing's output device and returns its
    /// handle (0 on failure). The engine adds every deck / mic / sweeper channel to this
    /// handle, so the rest of the engine is agnostic to which backend produced it.
    /// </summary>
    int CreateMixer(ResolvedRouting routing);

    /// <summary>Begins rendering the mixer to the output device.</summary>
    void Start();

    /// <summary>Stops rendering and frees the mixer / output resources.</summary>
    void Stop();

    /// <summary>
    /// Non-destructive master output level for the VU meter, packed as BASS_ChannelGetLevel
    /// does (LOWORD = left, HIWORD = right, 0..32768); -1 on failure. Must NOT consume audio:
    /// on a played mixer (DirectSound) BASS_ChannelGetLevel reads the playback buffer, but on a
    /// DECODE mixer (WASAPI) it would decode-and-remove data — so WASAPI reads the level from the
    /// WASAPI device instead.
    /// </summary>
    int GetOutputLevel();
}
