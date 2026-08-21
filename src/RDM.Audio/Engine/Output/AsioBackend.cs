using System;
using Microsoft.Extensions.Logging;
using RDM.Shared.Enums;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Mix;
using Un4seen.BassAsio;

namespace RDM.Audio.Engine.Output;

/// <summary>
/// ASIO output backend. The master mix is a BASS_STREAM_DECODE mixer fed straight into the ASIO
/// driver via BASS_ASIO_ChannelEnableBASS (BASS pulls the mixer internally — no manual proc). ASIO
/// bypasses the Windows mixer entirely and runs at the driver's native rate, so it removes the
/// 44.1↔48 resample regardless of Windows settings, at the lowest latency of all backends.
///
/// The real output device is owned by ASIO — <see cref="RoutingGraph"/> inits only the No Sound
/// device so the decode graph can exist. Generic USB CODEC cards usually expose ASIO only through
/// ASIO4ALL; if no ASIO driver is present, init fails with a clear error (pick another mode).
/// </summary>
internal sealed class AsioBackend : IOutputBackend
{
    private readonly ILogger _log;
    private int _asioDevice = -1;
    private int _mixerHandle;
    private int _outChannel;   // first ASIO output channel of the enabled stereo pair

    public AsioBackend(DriverType requestedMode, ILogger log)
    {
        RequestedMode = requestedMode;
        _log          = log;
    }

    public DriverType RequestedMode { get; }
    public DriverType EffectiveMode => DriverType.Asio;

    public int CreateMixer(ResolvedRouting routing)
    {
        _asioDevice = ResolveAsioDevice(routing.PlayerFriendlyName);
        if (_asioDevice < 0)
            throw new AudioEngineException(
                "Brak sterownika ASIO — zainstaluj ASIO4ALL lub sterownik karty, albo wybierz inny tryb wyjścia.");

        if (!BassAsio.BASS_ASIO_Init(_asioDevice, BASSASIOInit.BASS_ASIO_THREAD))
            throw new AudioEngineException(
                $"BASS_ASIO_Init failed (device {_asioDevice}): {BassAsio.BASS_ASIO_ErrorGetCode()}");

        // Ask for the configured rate, then read back what the driver actually runs at and build
        // the mixer to match exactly (a mismatch would misalign samples → distortion).
        BassAsio.BASS_ASIO_SetRate(routing.SampleRate);
        double rate = BassAsio.BASS_ASIO_GetRate();
        int freq    = rate > 0 ? (int)rate : routing.SampleRate;
        int chans   = routing.Channels;

        _mixerHandle = BassMix.BASS_Mixer_StreamCreate(
            freq, chans, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT);
        if (_mixerHandle == 0)
        {
            var err = Bass.BASS_ErrorGetCode();
            BassAsio.BASS_ASIO_Free();
            _asioDevice = -1;
            throw new AudioEngineException($"ASIO: BASS_Mixer_StreamCreate failed: {err}");
        }

        // Pick the ASIO output channel. With ASIO4ALL (a WDM wrapper) each physical endpoint is a
        // separate channel pair, so channel 0 is NOT necessarily the wanted card — match by name.
        int outCh = ResolveOutputChannel(routing.PlayerFriendlyName);

        // Feed the decode mixer into the chosen channel; join=true attaches the next channels to
        // match the mixer's channel count (stereo → outCh + outCh+1).
        if (!BassAsio.BASS_ASIO_ChannelEnableBASS(false, outCh, _mixerHandle, true))
        {
            var err = BassAsio.BASS_ASIO_ErrorGetCode();
            Bass.BASS_StreamFree(_mixerHandle);
            _mixerHandle = 0;
            BassAsio.BASS_ASIO_Free();
            _asioDevice = -1;
            throw new AudioEngineException($"BASS_ASIO_ChannelEnableBASS failed: {err}");
        }
        _outChannel = outCh;

        _log.LogInformation(
            "AsioBackend: device={Idx} channel={Ch} freq={Freq} chans={Chans}",
            _asioDevice, outCh, freq, chans);

        return _mixerHandle;
    }

    public void Start()
    {
        BassAsio.BASS_ASIO_SetDevice(_asioDevice);

        // ASIO4ALL only opens the WDM endpoints (groups) that have an enabled channel. If the USB
        // CODEC group is busy/not activated, Start fails silently unless we check — log it so a dead
        // output is diagnosable instead of just "no sound".
        bool ok = BassAsio.BASS_ASIO_Start(0, 0);   // buflen 0 = driver default; threads 0 = auto
        if (!ok)
        {
            _log.LogWarning("AsioBackend: BASS_ASIO_Start failed: {Err} — kanał {Ch} może być zajęty " +
                "lub grupa nieaktywna w panelu ASIO4ALL.", BassAsio.BASS_ASIO_ErrorGetCode(), _outChannel);
            return;
        }

        var ci = BassAsio.BASS_ASIO_ChannelGetInfo(false, _outChannel);
        _log.LogInformation(
            "AsioBackend: started=true rate={Rate} channel={Ch} name='{Name}' active={Active}",
            BassAsio.BASS_ASIO_GetRate(), _outChannel, ci?.name,
            BassAsio.BASS_ASIO_ChannelIsActive(false, _outChannel));
    }

    public void Stop()
    {
        if (_asioDevice >= 0)
        {
            BassAsio.BASS_ASIO_SetDevice(_asioDevice);
            BassAsio.BASS_ASIO_Stop();
            BassAsio.BASS_ASIO_Free();
            _asioDevice = -1;
        }
        if (_mixerHandle != 0)
        {
            Bass.BASS_StreamFree(_mixerHandle);
            _mixerHandle = 0;
        }
    }

    private long _lastLevelLogTick;

    // Non-destructive: ASIO tracks the output level of enabled channels itself.
    public int GetOutputLevel()
    {
        if (_asioDevice < 0) return -1;
        BassAsio.BASS_ASIO_SetDevice(_asioDevice);
        float l = BassAsio.BASS_ASIO_ChannelGetLevel(false, _outChannel);
        float r = BassAsio.BASS_ASIO_ChannelGetLevel(false, _outChannel + 1);

        // Throttled diagnostic: is ASIO actually emitting samples? >0 → BASS feeds ASIO (silence is
        // hardware/ASIO4ALL routing); ~0/-1 while a track plays → the decode mixer isn't being pulled.
        long now = Environment.TickCount64;
        if (now - _lastLevelLogTick >= 2000)
        {
            _lastLevelLogTick = now;
            _log.LogInformation("AsioBackend: level ch{Ch} L={L:F4} R={R:F4} isStarted={St}",
                _outChannel, l, r, BassAsio.BASS_ASIO_IsStarted());
        }

        if (l < 0f && r < 0f) return -1;

        int li = (int)(Math.Clamp(l < 0f ? 0f : l, 0f, 1f) * 32768f);
        int ri = (int)(Math.Clamp(r < 0f ? 0f : r, 0f, 1f) * 32768f);
        return (ri << 16) | (li & 0xFFFF);
    }

    /// <summary>
    /// Picks the ASIO output channel matching the player device by name. With ASIO4ALL every WDM
    /// endpoint is a separate channel pair, so channel 0 is often the wrong card. Logs all output
    /// channels and falls back to channel 0 if no name matches.
    /// </summary>
    private int ResolveOutputChannel(string? friendlyName)
    {
        var info    = BassAsio.BASS_ASIO_GetInfo();
        int outputs = info?.outputs ?? 0;
        if (outputs <= 0) return 0;

        // Normalize the friendly name once: collapse whitespace so "USB Audio CODEC  1" (ASIO's
        // double space) lines up with "USB Audio CODEC" inside "Player (2 — USB Audio CODEC )".
        string want = Normalize(friendlyName);
        int match = -1;

        for (int ch = 0; ch < outputs; ch++)
        {
            var ci   = BassAsio.BASS_ASIO_ChannelGetInfo(false, ch);
            string cn = ci?.name?.Trim() ?? "";
            _log.LogDebug("AsioBackend: ASIO output channel[{Ch}] name='{Name}' group={Grp}",
                ch, cn, ci?.group);

            // ASIO4ALL names each channel of a pair with a trailing index ("USB Audio CODEC  1"/"2");
            // strip it so the device core matches the Windows friendly name (which has no index).
            string core = System.Text.RegularExpressions.Regex.Replace(Normalize(cn), @"\s+\d+$", "");

            if (match < 0 && want.Length > 0 && core.Length > 0
                && (want.Contains(core, StringComparison.OrdinalIgnoreCase)
                    || core.Contains(want, StringComparison.OrdinalIgnoreCase)))
                match = ch;
        }

        if (match < 0)
        {
            _log.LogWarning(
                "AsioBackend: nie dopasowano kanału ASIO do '{Name}' — używam kanału 0. Wskaż wyjście " +
                "w panelu ASIO4ALL, albo użyj WASAPI exclusive (celuje wprost w kartę).", friendlyName);
            return 0;
        }

        if ((match & 1) == 1) match -= 1;   // snap to the left channel of the stereo pair
        return match;
    }

    /// <summary>Collapses runs of whitespace to a single space and trims — ASIO4ALL pads channel
    /// names with double spaces that would otherwise break substring matching.</summary>
    private static string Normalize(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? ""
            : System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");

    /// <summary>
    /// Maps the configured player device to an ASIO driver index. ASIO devices are drivers
    /// (e.g. "ASIO4ALL v2"), not Windows endpoints, so a name match rarely succeeds — we fall back
    /// to the first ASIO driver.
    /// </summary>
    private int ResolveAsioDevice(string? friendlyName)
    {
        var infos = BassAsio.BASS_ASIO_GetDeviceInfos();
        if (infos is null || infos.Length == 0) return -1;

        string? wantName = friendlyName?.Trim();
        for (int i = 0; i < infos.Length; i++)
        {
            _log.LogDebug("AsioBackend: ASIO device[{Idx}] name='{Name}' driver='{Driver}'",
                i, infos[i]?.name, infos[i]?.driver);
            if (!string.IsNullOrEmpty(wantName)
                && string.Equals(infos[i]?.name?.Trim(), wantName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        _log.LogWarning(
            "AsioBackend: brak dopasowania nazwy '{Name}' — używam pierwszego sterownika ASIO '{Drv}' (idx=0).",
            friendlyName, infos[0]?.name);
        return 0;
    }
}
