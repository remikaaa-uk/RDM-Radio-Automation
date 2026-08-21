using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RDM.Shared.Enums;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Mix;
using Un4seen.BassWasapi;

namespace RDM.Audio.Engine.Output;

/// <summary>
/// WASAPI output backend (shared or exclusive). The master mix is a BASS_STREAM_DECODE mixer;
/// a <see cref="WASAPIPROC"/> pulls float data from it into the WASAPI device. The real output
/// device is owned by WASAPI (NOT opened via DirectSound) — <see cref="RoutingGraph"/> inits only
/// the "No Sound" device so the decode graph (tracks, sweeper, mic-push, mixer) can exist.
///
/// Shared:    runs at the Windows shared mix format (mixfreq). Removes the 44.1↔48 resample only
///            when the Windows format already equals the hardware-native rate (e.g. both 48000).
/// Exclusive: runs at settings.SampleRate, verified via BASS_WASAPI_CheckFormat; bypasses the
///            Windows mixer entirely (bit-exact) but locks the device for other apps.
/// </summary>
internal sealed class WasapiBackend : IOutputBackend
{
    private readonly ILogger _log;
    private readonly bool    _exclusive;

    private int         _mixerHandle;
    private int         _wasapiDevice = -1;
    private WASAPIPROC? _wasapiProc;
    private GCHandle    _procGch;

    // VU level measured inside the WASAPIPROC from the exact data going out — packed like
    // BASS_ChannelGetLevel (LOWORD=left, HIWORD=right, 0..32768). Written on the WASAPI thread,
    // read on the UI thread; volatile int gives an atomic hand-off. _peakBuf is reused to avoid
    // per-callback allocation in the audio thread.
    private volatile int _levelPacked;
    private float[]      _peakBuf = Array.Empty<float>();
    private int          _outChans = 2;

    public WasapiBackend(DriverType requestedMode, ILogger log)
    {
        RequestedMode = requestedMode;
        _exclusive    = requestedMode == DriverType.WasapiExclusive;
        _log          = log;
    }

    public DriverType RequestedMode { get; }

    public DriverType EffectiveMode =>
        _exclusive ? DriverType.WasapiExclusive : DriverType.WasapiShared;

    public int CreateMixer(ResolvedRouting routing)
    {
        _wasapiDevice = ResolveWasapiDevice(routing.PlayerSystemDeviceId, routing.PlayerFriendlyName);
        if (_wasapiDevice < 0)
            throw new AudioEngineException("WASAPI: nie znaleziono urządzenia wyjściowego.");

        var di = BassWasapi.BASS_WASAPI_GetDeviceInfo(_wasapiDevice);

        int freq;
        int chans;
        BASSWASAPIInit flags;

        if (_exclusive)
        {
            freq  = routing.SampleRate;
            chans = routing.Channels;
            flags = BASSWASAPIInit.BASS_WASAPI_EXCLUSIVE | BASSWASAPIInit.BASS_WASAPI_EVENT;

            var fmt = BassWasapi.BASS_WASAPI_CheckFormat(_wasapiDevice, freq, chans, flags);
            if (fmt == BASSWASAPIFormat.BASS_WASAPI_FORMAT_UNKNOWN)
                throw new AudioEngineException(
                    $"WASAPI exclusive: urządzenie nie obsługuje {freq} Hz / {chans} ch. " +
                    "Zmień częstotliwość w ustawieniach lub użyj trybu współdzielonego.");
        }
        else
        {
            // Shared: follow the Windows shared mix format so nothing resamples on our side.
            freq  = di is not null && di.mixfreq  > 0 ? di.mixfreq  : routing.SampleRate;
            chans = di is not null && di.mixchans > 0 ? di.mixchans : routing.Channels;
            flags = default; // shared (no EXCLUSIVE flag)
        }

        // Init WASAPI FIRST, then build the mixer to the ACTUAL negotiated format. Creating the
        // mixer up-front on assumed freq/chans risks a mismatch with what WASAPI really runs at →
        // misaligned samples → distortion. buffer: shared gets headroom so the synchronous decode
        // in WasapiPull doesn't underrun on CPU/GC jitter; exclusive uses EVENT + device period.
        _wasapiProc = WasapiPull;
        _procGch    = GCHandle.Alloc(_wasapiProc);

        float buffer = _exclusive ? 0f : 0.4f;   // seconds; 0 = device default

        bool ok = BassWasapi.BASS_WASAPI_Init(
            _wasapiDevice, freq, chans, flags,
            buffer, 0f,
            _wasapiProc, IntPtr.Zero);

        if (!ok)
        {
            var err = Bass.BASS_ErrorGetCode();
            FreeProc();
            throw new AudioEngineException(
                $"BASS_WASAPI_Init failed ({(_exclusive ? "exclusive" : "shared")}): {err}");
        }

        // Query the format WASAPI actually negotiated and match the mixer to it exactly.
        BassWasapi.BASS_WASAPI_SetDevice(_wasapiDevice);
        var wi         = BassWasapi.BASS_WASAPI_GetInfo();
        int actualFreq = wi is not null && wi.freq  > 0 ? wi.freq  : freq;
        int actualChans= wi is not null && wi.chans > 0 ? wi.chans : chans;
        _outChans      = actualChans;

        // Decode mixer — pulled by the WASAPIPROC, never played directly.
        _mixerHandle = BassMix.BASS_Mixer_StreamCreate(
            actualFreq, actualChans, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT);
        if (_mixerHandle == 0)
        {
            var err = Bass.BASS_ErrorGetCode();
            BassWasapi.BASS_WASAPI_Free();
            FreeProc();
            throw new AudioEngineException($"WASAPI: BASS_Mixer_StreamCreate failed: {err}");
        }

        _log.LogInformation(
            "WasapiBackend: device={Idx} '{Name}' mode={Mode} requested={ReqFreq}/{ReqCh} " +
            "negotiated={Freq}/{Ch} format={Fmt} buflen={Buf}s",
            _wasapiDevice, di?.name, EffectiveMode, freq, chans,
            actualFreq, actualChans, wi?.format, wi?.buflen);

        return _mixerHandle;
    }

    public void Start()
    {
        BassWasapi.BASS_WASAPI_SetDevice(_wasapiDevice);
        BassWasapi.BASS_WASAPI_Start();
    }

    public void Stop()
    {
        if (_wasapiDevice >= 0)
        {
            BassWasapi.BASS_WASAPI_SetDevice(_wasapiDevice);
            BassWasapi.BASS_WASAPI_Stop(true);
            BassWasapi.BASS_WASAPI_Free();
            _wasapiDevice = -1;
        }
        if (_mixerHandle != 0)
        {
            Bass.BASS_StreamFree(_mixerHandle);
            _mixerHandle = 0;
        }
        _levelPacked = 0;
        FreeProc();
    }

    // Returns the peak measured in the WASAPIPROC from the actual output data — never touches the
    // decode mixer, so it can't steal audio. -1 before the device is up.
    public int GetOutputLevel() => _wasapiDevice < 0 ? -1 : _levelPacked;

    /// <summary>WASAPI pulls output data through this — feed it straight from the decode mixer.</summary>
    private int WasapiPull(IntPtr buffer, int length, IntPtr user)
    {
        int got = Bass.BASS_ChannelGetData(_mixerHandle, buffer, length);
        if (got > 0) MeasurePeak(buffer, got);
        return got < 0 ? 0 : got;
    }

    /// <summary>Peak-scan the float output buffer for the VU meter (runs on the WASAPI thread).</summary>
    private void MeasurePeak(IntPtr buffer, int bytes)
    {
        int floatCount = bytes / sizeof(float);
        if (floatCount <= 0) return;
        if (_peakBuf.Length < floatCount) _peakBuf = new float[floatCount];
        Marshal.Copy(buffer, _peakBuf, 0, floatCount);

        float peakL = 0f, peakR = 0f;
        if (_outChans >= 2)
        {
            for (int i = 0; i + 1 < floatCount; i += 2)
            {
                float l = Math.Abs(_peakBuf[i]);
                float r = Math.Abs(_peakBuf[i + 1]);
                if (l > peakL) peakL = l;
                if (r > peakR) peakR = r;
            }
        }
        else
        {
            for (int i = 0; i < floatCount; i++)
            {
                float v = Math.Abs(_peakBuf[i]);
                if (v > peakL) peakL = v;
            }
            peakR = peakL;
        }

        int li = (int)(Math.Min(peakL, 1f) * 32768f);
        int ri = (int)(Math.Min(peakR, 1f) * 32768f);
        _levelPacked = (ri << 16) | (li & 0xFFFF);
    }

    /// <summary>
    /// Maps the configured player device to a WASAPI output device index. BASS's DirectSound
    /// enumeration reports a device-interface path as <c>.id</c> (e.g. <c>usb#vid_…#{guid}\global</c>)
    /// while WASAPI reports an MMDevice endpoint id (<c>{0.0.0.…}.{guid}</c>) — the two never match
    /// as strings. So we match primarily by friendly name (both come from the same Windows name),
    /// try id as a bonus, and only then fall back to the default output.
    /// </summary>
    private int ResolveWasapiDevice(string? systemDeviceId, string? friendlyName)
    {
        var infos = BassWasapi.BASS_WASAPI_GetDeviceInfos();
        if (infos is null || infos.Length == 0) return -1;

        int firstEnabledOutput = -1;
        int defaultOutput      = -1;
        int idMatch            = -1;
        int nameMatch          = -1;

        string? wantName = friendlyName?.Trim();

        for (int i = 0; i < infos.Length; i++)
        {
            var d = infos[i];
            if (d is null || d.IsInput || d.IsLoopback || !d.IsEnabled) continue;

            _log.LogDebug("WasapiBackend: WASAPI output[{Idx}] name='{Name}' id='{Id}' default={Def}",
                i, d.name, d.id, d.IsDefault);

            if (firstEnabledOutput < 0) firstEnabledOutput = i;
            if (d.IsDefault && defaultOutput < 0) defaultOutput = i;

            if (idMatch < 0 && !string.IsNullOrEmpty(systemDeviceId)
                && string.Equals(d.id, systemDeviceId, StringComparison.OrdinalIgnoreCase))
                idMatch = i;

            if (nameMatch < 0 && !string.IsNullOrEmpty(wantName)
                && string.Equals(d.name?.Trim(), wantName, StringComparison.OrdinalIgnoreCase))
                nameMatch = i;
        }

        if (idMatch >= 0)   return idMatch;
        if (nameMatch >= 0) return nameMatch;

        int fallback = defaultOutput >= 0 ? defaultOutput : firstEnabledOutput;
        if (fallback >= 0)
            _log.LogWarning(
                "WasapiBackend: urządzenie playera (name='{Name}', id='{Id}') nie dopasowane do " +
                "urządzenia WASAPI — używam domyślnego (idx={Idx}). Sprawdź nazwę urządzenia w Ustawieniach.",
                friendlyName, systemDeviceId, fallback);
        return fallback;
    }

    private void FreeProc()
    {
        if (_procGch.IsAllocated) _procGch.Free();
        _wasapiProc = null;
    }
}
