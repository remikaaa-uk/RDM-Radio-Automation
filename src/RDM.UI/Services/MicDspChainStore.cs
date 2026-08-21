using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace RDM.UI.Services;

/// <summary>
/// Persists the microphone DSP chain — the built-in FX slots, the VST plugins and each plugin's
/// own settings — in the <c>mic_dsp</c> section of rdm.config.json.
/// </summary>
/// <remarks>
/// The engine keeps the chain in plain in-memory lists, so before this existed everything a user
/// set up was gone at the next start. It lives in the machine-local config rather than the shared
/// database on purpose: a slot points at a VST DLL by absolute path, which only means anything on
/// the machine where that plugin is installed.
/// </remarks>
public sealed class MicDspChainStore
{
    private const string SectionName = "mic_dsp";

    private readonly SettingsConfigService        _config;
    private readonly IAudioEngine                 _engine;
    private readonly ILogger<MicDspChainStore>    _logger;

    public MicDspChainStore(
        SettingsConfigService     config,
        IAudioEngine              engine,
        ILogger<MicDspChainStore> logger)
    {
        _config = config;
        _engine = engine;
        _logger = logger;
    }

    /// <summary>
    /// Rebuilds the chain in the engine from the config. Called once at startup, before the mic
    /// can be turned on — the slots are configuration only, the plugins load when the mic starts.
    /// </summary>
    public async Task RestoreAsync()
    {
        JsonObject root;
        try
        {
            root = await _config.LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MicDspChainStore: cannot read the config — starting with an empty chain");
            return;
        }

        if (root[SectionName] is not JsonObject section)
        {
            _logger.LogInformation("MicDspChainStore: no '{Section}' section — nothing to restore", SectionName);
            return;
        }

        int fxRestored = 0, vstRestored = 0;

        foreach (var node in section["fx"] as JsonArray ?? [])
        {
            var typeName = node?["type"]?.GetValue<string>();
            if (!Enum.TryParse<MicFxType>(typeName, out var fxType))
            {
                _logger.LogWarning("MicDspChainStore: unknown FX type '{Type}' — slot skipped", typeName);
                continue;
            }

            try
            {
                int slotId = await _engine.AddMicFxAsync(fxType);

                if (node?["params"] is JsonObject stored)
                {
                    var values = new Dictionary<string, float>();
                    foreach (var (key, value) in stored)
                        if (value is not null) values[key] = value.GetValue<float>();

                    // Goes through the engine so unknown keys are dropped and values clamped —
                    // a hand-edited config cannot push a bad number into BASS.
                    await _engine.UpdateMicFxAsync(slotId, values);
                }

                fxRestored++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MicDspChainStore: cannot restore FX '{Type}'", typeName);
            }
        }

        foreach (var node in section["vst"] as JsonArray ?? [])
        {
            var dllPath = node?["dll_path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(dllPath)) continue;

            try
            {
                // A missing DLL throws here (the plugin was uninstalled or the drive is offline).
                // That must cost this one slot only — the rest of the chain still restores.
                int slotId = await _engine.AddMicVstAsync(dllPath);

                var slot = _engine.GetMicVstList().FirstOrDefault(s => s.SlotId == slotId);
                if (slot is not null)
                {
                    // Applied when the plugin actually loads, i.e. when the mic is switched on.
                    if (node?["state_chunk"]?.GetValue<string>() is { Length: > 0 } base64)
                        slot.StateChunk = Convert.FromBase64String(base64);

                    if (node?["name"]?.GetValue<string>() is { Length: > 0 } name)
                        slot.PluginName = name;
                }

                vstRestored++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MicDspChainStore: cannot restore VST '{Dll}'", dllPath);
            }
        }

        _logger.LogInformation("MicDspChainStore: restored {Fx} FX and {Vst} VST slots", fxRestored, vstRestored);
    }

    /// <summary>
    /// Writes the current chain back to the config. Captures the plugins' settings first, which
    /// only succeeds while the mic is on; with the mic off the previously captured state is kept
    /// rather than overwritten with nothing.
    /// </summary>
    public async Task SaveAsync()
    {
        try
        {
            _engine.CaptureMicVstStates();

            var fx = new JsonArray();
            foreach (var slot in _engine.GetMicFxList())
            {
                var parameters = new JsonObject();
                foreach (var (key, value) in slot.Parameters)
                {
                    // Guard against a non-finite value ever reaching the serializer again — that
                    // is exactly what once wiped the whole file.
                    if (float.IsFinite(value)) parameters[key] = value;
                }

                fx.Add(new JsonObject
                {
                    ["type"]   = slot.FxType.ToString(),
                    ["params"] = parameters
                });
            }

            var vst = new JsonArray();
            foreach (var slot in _engine.GetMicVstList())
            {
                // Per slot on purpose: a plugin whose state cannot be serialised must cost its own
                // settings at most. It once cost the whole file — the list of plugins vanished
                // with it — because one plugin reported infinity among its parameters.
                try
                {
                    var entry = new JsonObject
                    {
                        ["dll_path"] = slot.DllPath,
                        ["name"]     = slot.PluginName
                    };

                    if (slot.StateChunk is { Length: > 0 } chunk)
                        entry["state_chunk"] = Convert.ToBase64String(chunk);

                    vst.Add(entry);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MicDspChainStore: cannot serialise slot {SlotId} '{Name}' — " +
                        "storing it without its state", slot.SlotId, slot.PluginName);
                    vst.Add(new JsonObject
                    {
                        ["dll_path"] = slot.DllPath,
                        ["name"]     = slot.PluginName
                    });
                }
            }

            var root = await _config.LoadAsync();
            root[SectionName] = new JsonObject { ["fx"] = fx, ["vst"] = vst };
            await _config.SaveAsync(root);

            _logger.LogInformation("MicDspChainStore: saved {Fx} FX and {Vst} VST slots", fx.Count, vst.Count);
        }
        catch (Exception ex)
        {
            // Never let persistence break the caller — this runs from window close and shutdown.
            _logger.LogWarning(ex, "MicDspChainStore: saving the chain failed");
        }
    }
}
