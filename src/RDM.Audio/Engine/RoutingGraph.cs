using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Shared.Enums;
using Un4seen.Bass;

namespace RDM.Audio.Engine;

/// <summary>
/// Resolves AudioSettings device UUIDs to BASS device indices and manages
/// per-device BASS_Init / BASS_Free lifecycle.
/// Does not access the database — receives AudioDevice list from the caller.
/// </summary>
internal sealed class RoutingGraph : IDisposable
{
    // device index → true if WE called BASS_Init (we own the Free)
    private readonly Dictionary<int, bool> _devices = new();
    private readonly ILogger _log;
    private bool _disposed;

    public RoutingGraph(ILogger log) => _log = log;

    // ── Public API ───────────────────────────────────────────────────────────

    public ResolvedRouting Initialize(AudioSettings settings, IReadOnlyList<AudioDevice> knownDevices)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bassLookup = BuildBassLookup();      // systemDeviceId → BASS index
        int sampleRate = (int)settings.SampleRate;
        var mode          = settings.OutputMode;
        bool ownsDeviceExt = mode is DriverType.WasapiShared
                                  or DriverType.WasapiExclusive
                                  or DriverType.Asio;

        var playerDev = settings.DevicePlayerId is not null
            ? knownDevices.FirstOrDefault(d => d.DeviceId == settings.DevicePlayerId)
            : null;

        // Assignments pointing at the card an exclusive backend already owns. These resolve to a
        // perfectly valid — but permanently silent — BASS device, so they are dropped here and the
        // affected sources fall back to the program bus.
        var blocked = FindExclusiveCardCollisions(settings, knownDevices, mode, playerDev);

        // The player output device:
        //  • DirectSound  → BASS_Init the real device; its BASS index feeds the mixer/ChannelPlay.
        //  • WASAPI/ASIO  → the backend owns the real device (WASAPI exclusive / ASIO lock it), so we
        //                   must NOT open it via DirectSound. Init the "No Sound" device (0) only so
        //                   decode streams (mixer, tracks, sweeper, mic-push) can be created; the
        //                   backend resolves the real output device itself.
        int player;
        if (ownsDeviceExt)
        {
            EnsureInit(0, sampleRate);   // No Sound device — decode context only
            player = 0;
        }
        else
        {
            player = Resolve(settings.DevicePlayerId, knownDevices, bassLookup, sampleRate);
        }

        // Cartwall/PFL stay on DirectSound in every mode (they play via BASS_ChannelPlay on their
        // own devices). Under WASAPI, if they fall back to the player device they land on No Sound
        // (index 0) → silent; that limitation is documented (dedicated devices required).
        int? cartwall = settings.DeviceCartwallId is not null && !blocked.Contains(settings.DeviceCartwallId)
            ? Resolve(settings.DeviceCartwallId, knownDevices, bassLookup, sampleRate)
            : null;
        int? pfl      = settings.DevicePflId is not null
            ? Resolve(settings.DevicePflId, knownDevices, bassLookup, sampleRate)
            : null;

        // Optional dedicated output cards for the sweeper and the four AUX decks. Unlike Player/PFL
        // these are best-effort: a null/unknown/uninitialisable device resolves to null so the source
        // simply stays on the program bus (mixer/player) instead of crashing engine init. An assigned
        // card plays via BASS_ChannelPlay on its own DirectSound device; if it names the card an
        // exclusive backend already owns, `blocked` drops it here so the source falls back to the bus
        // instead of opening a valid-looking but permanently silent second connection.
        int? sweeper = ResolveOptional("Sweeper", settings.DeviceSweeperId, knownDevices, bassLookup, sampleRate, blocked);
        var  auxIds  = new[] { settings.DeviceAux1Id, settings.DeviceAux2Id, settings.DeviceAux3Id, settings.DeviceAux4Id };
        var  auxIdx  = new int?[4];
        for (int i = 0; i < 4; i++)
            auxIdx[i] = ResolveOptional($"AUX{i + 1}", auxIds[i], knownDevices, bassLookup, sampleRate, blocked);

        return new ResolvedRouting(
            PlayerDeviceIndex:    player,
            CartwallDeviceIndex:  cartwall,
            PflDeviceIndex:       pfl,
            SweeperDeviceIndex:   sweeper,
            AuxDeviceIndices:     auxIdx,
            SampleRate:           sampleRate,
            Channels:             2,
            Mode:                 mode,
            PlayerSystemDeviceId: playerDev?.SystemDeviceId,
            PlayerFriendlyName:   playerDev?.FriendlyName);
    }

    /// <summary>
    /// Under ASIO / WASAPI-exclusive the backend owns the player's card outright. A source assigned to
    /// that SAME card still opens a second DirectSound handle successfully — BASS_Init returns no error
    /// and the device reports as available — but nothing ever reaches the DAC. No layer below this one
    /// can detect the condition, so it is caught here or not at all.
    /// Returns the device IDs to drop: the affected sources then resolve to null and fall back to the
    /// program bus, which under an exclusive backend is the only path that actually reaches the card.
    /// Compares physical cards (SystemDeviceId), not device rows — the same card can be stored under
    /// several UUIDs, and exclusivity is a property of the hardware.
    /// WASAPI *shared* is deliberately excluded: it lets other apps onto the card by design, so a second
    /// handle there is legitimate and dropping it would break a working setup.
    /// </summary>
    private HashSet<string> FindExclusiveCardCollisions(
        AudioSettings settings,
        IReadOnlyList<AudioDevice> knownDevices,
        DriverType mode,
        AudioDevice? playerDev)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (mode is not (DriverType.Asio or DriverType.WasapiExclusive)) return blocked;
        if (playerDev?.SystemDeviceId is not { Length: > 0 } playerCard) return blocked;

        AudioDevice? Colliding(string? deviceId)
        {
            if (deviceId is null) return null;
            var dev = knownDevices.FirstOrDefault(d => d.DeviceId == deviceId);
            return dev is not null
                && string.Equals(dev.SystemDeviceId, playerCard, StringComparison.OrdinalIgnoreCase)
                    ? dev
                    : null;
        }

        foreach (var (role, deviceId) in new[]
                 {
                     ("Sweeper",  settings.DeviceSweeperId),
                     ("AUX1",     settings.DeviceAux1Id),
                     ("AUX2",     settings.DeviceAux2Id),
                     ("AUX3",     settings.DeviceAux3Id),
                     ("AUX4",     settings.DeviceAux4Id),
                     ("Cartwall", settings.DeviceCartwallId),
                 })
        {
            if (Colliding(deviceId) is not { } dev) continue;

            blocked.Add(deviceId!);
            _log.LogWarning(
                "{Role}: assigned to '{Name}' — the same physical card the {Mode} backend already owns "
                + "exclusively. A second connection opens without error but stays silent, so the "
                + "assignment is ignored and the source stays on the program bus.",
                role, dev.FriendlyName, mode);
        }

        // PFL is off-air by definition, so it has no program-bus fallback — it needs a real second card.
        if (Colliding(settings.DevicePflId) is { } pflDev)
            _log.LogWarning(
                "PFL: assigned to '{Name}' — the same physical card the {Mode} backend already owns "
                + "exclusively, so it will stay silent. PFL cannot fall back to the program bus "
                + "(it must stay off-air) — assign a different card.",
                pflDev.FriendlyName, mode);

        return blocked;
    }

    /// <summary>
    /// Frees every BASS device that this instance initialized.
    /// Call from BassAudioEngine.ShutdownAsync before BASS_StreamFree of the mixer.
    /// </summary>
    public void Shutdown()
    {
        foreach (var (index, owned) in _devices)
        {
            if (!owned) continue;
            Bass.BASS_SetDevice(index);
            Bass.BASS_Free();
        }
        _devices.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Shutdown();
        _disposed = true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a device UUID from AudioSettings → BASS index, then ensures BASS_Init.
    /// Falls back to default device (-1) if UUID is null or device not found in BASS.
    /// Uses composite key "systemId|friendlyName" so two endpoints on the same hardware
    /// (e.g. "Wyjście cyfrowe" and "PFL" sharing an info.id) resolve to distinct BASS indices.
    /// </summary>
    private int Resolve(
        string? deviceId,
        IReadOnlyList<AudioDevice> knownDevices,
        Dictionary<string, int> bassLookup,
        int sampleRate)
    {
        int requestedIndex = -1;

        if (deviceId is not null)
        {
            var known = knownDevices.FirstOrDefault(d => d.DeviceId == deviceId);
            if (known is not null)
            {
                // Try composite key first; fall back to id-only for devices without name conflicts.
                var composite = $"{known.SystemDeviceId}|{known.FriendlyName?.Trim()}";
                if (!bassLookup.TryGetValue(composite, out int idx))
                    bassLookup.TryGetValue(known.SystemDeviceId, out idx);
                // idx == 0 means BASS "No Sound" device → treat as not found, keep fallback -1
                if (idx > 0)
                    requestedIndex = idx;
            }
        }

        return EnsureInit(requestedIndex, sampleRate);
    }

    /// <summary>
    /// Best-effort device resolution for optional routes (sweeper, AUX decks). Returns null when the
    /// UUID is null, not a known device, not present in BASS (e.g. unplugged), or fails to initialise —
    /// so the caller keeps the source on the program bus rather than aborting. Never throws.
    /// Every fallback except "not assigned" is logged: a card held exclusively by the active
    /// WASAPI/ASIO backend is otherwise indistinguishable from an empty assignment in rdm.log.
    /// </summary>
    private int? ResolveOptional(
        string role,
        string? deviceId,
        IReadOnlyList<AudioDevice> knownDevices,
        Dictionary<string, int> bassLookup,
        int sampleRate,
        IReadOnlySet<string> blockedDeviceIds)
    {
        if (deviceId is null) return null;   // not assigned — normal, stays on the program bus

        // Collides with a card an exclusive backend owns; already logged by the collision scan.
        if (blockedDeviceIds.Contains(deviceId)) return null;

        var known = knownDevices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (known is null)
        {
            _log.LogWarning("{Role}: assigned device '{DeviceId}' is not in the known device list → program bus",
                role, deviceId);
            return null;
        }

        var composite = $"{known.SystemDeviceId}|{known.FriendlyName?.Trim()}";
        if (!bassLookup.TryGetValue(composite, out int idx))
            bassLookup.TryGetValue(known.SystemDeviceId, out idx);
        if (idx <= 0)                // not found or BASS "No Sound"
        {
            _log.LogWarning(
                "{Role}: device '{Name}' (id '{SysId}') is assigned but not present in BASS — unplugged, "
                + "or held exclusively by the active output backend → program bus",
                role, known.FriendlyName, known.SystemDeviceId);
            return null;
        }

        try
        {
            return EnsureInit(idx, sampleRate);
        }
        catch (Exception ex)         // unplugged / locked card → fall back to program bus
        {
            _log.LogWarning("{Role}: BASS_Init failed for '{Name}' (BASS index {Idx}): {Err} → program bus",
                role, known.FriendlyName, idx, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Calls BASS_Init for the requested device index if not already done.
    /// Returns the actual BASS device index (BASS_GetDevice after successful init).
    /// </summary>
    private int EnsureInit(int requestedIndex, int sampleRate)
    {
        // Check if we already managed this device
        if (_devices.ContainsKey(requestedIndex))
            return requestedIndex;

        // Check if BASS already has this device initialised by someone else
        var info = Bass.BASS_GetDeviceInfo(requestedIndex == -1 ? 1 : requestedIndex);
        bool alreadyInit = info is not null
            && (info.flags & BASSDeviceInfo.BASS_DEVICE_INIT) != 0;

        if (alreadyInit)
        {
            _devices[requestedIndex] = false; // not owned by us
            return requestedIndex;
        }

        bool ok = Bass.BASS_Init(
            device: requestedIndex,
            freq:   sampleRate,
            flags:  BASSInit.BASS_DEVICE_DEFAULT,
            win:    IntPtr.Zero);

        if (!ok)
        {
            BASSError err = Bass.BASS_ErrorGetCode();

            // BASS_ERROR_ALREADY is acceptable — another component inited this device
            if (err == BASSError.BASS_ERROR_ALREADY)
            {
                _devices[requestedIndex] = false;
                return requestedIndex;
            }

            throw new AudioEngineException(
                $"BASS_Init failed for device index {requestedIndex}: {err}");
        }

        // Capture the actual index BASS assigned (relevant when requestedIndex = -1)
        int actual = Bass.BASS_GetDevice();
        _devices[actual] = true; // we own this

        // Also record the requested key so callers using -1 get a cache hit next time
        if (requestedIndex != actual)
            _devices[requestedIndex] = false; // alias, not owned

        return actual;
    }

    /// <summary>
    /// Enumerates all BASS output devices and builds lookup → BASS index.
    /// Stores two keys per device:
    ///   "systemId|name"  — composite, lets two endpoints with the same info.id
    ///                      (e.g. "Wyjście cyfrowe" and "PFL") resolve to distinct indices.
    ///   "systemId"       — plain fallback for devices without name conflicts (first wins).
    /// </summary>
    private static Dictionary<string, int> BuildBassLookup()
    {
        var infos  = Bass.BASS_GetDeviceInfos();
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < infos.Length; i++)
        {
            var info = infos[i];
            if (info is null || string.IsNullOrEmpty(info.id)) continue;

            lookup.TryAdd($"{info.id}|{info.name?.Trim()}", i); // composite — always unique
            lookup.TryAdd(info.id, i);                          // plain — first occurrence wins
        }

        return lookup;
    }
}

// ── Value type returned to BassAudioEngine ───────────────────────────────────

internal record ResolvedRouting(
    int  PlayerDeviceIndex,
    int? CartwallDeviceIndex,
    int? PflDeviceIndex,
    int? SweeperDeviceIndex,
    int?[] AuxDeviceIndices,      // [0..3] = AUX 1..4; null = program bus
    int  SampleRate,
    int  Channels,
    DriverType Mode,
    string? PlayerSystemDeviceId,
    string? PlayerFriendlyName);
