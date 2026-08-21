using Avalonia.Input;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace RDM.UI.Services;

/// <summary>
/// Loads hotkeys.json and maps Key+Modifier combinations to abstract action tokens.
/// Action tokens are defined as constants in <see cref="HotkeyActions"/>.
/// </summary>
public sealed class HotkeyManagerService : IHotkeyManagerService
{
    private readonly Dictionary<HotkeyKey, string> _bindings = new();
    private readonly ILogger<HotkeyManagerService> _logger;

    public event Action<string>? ActionTriggered;

    public HotkeyManagerService(ILogger<HotkeyManagerService> logger) => _logger = logger;

    public async Task LoadAsync(string path = "hotkeys.json")
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("hotkeys.json not found at '{Path}' — no hotkeys registered", path);
            return;
        }

        try
        {
            var json     = await File.ReadAllTextAsync(path);
            var root     = JsonNode.Parse(json);
            var bindings = root?["bindings"]?.AsArray();
            if (bindings is null)
            {
                _logger.LogWarning("hotkeys.json has no 'bindings' array");
                return;
            }

            _bindings.Clear();

            foreach (var item in bindings)
            {
                if (item is null) continue;

                var keyStr = item["key"]?.GetValue<string>();
                var action = item["action"]?.GetValue<string>();
                if (keyStr is null || action is null) continue;

                if (!Enum.TryParse<Key>(keyStr, ignoreCase: true, out var key))
                {
                    _logger.LogWarning("Unknown Key in hotkeys.json: '{Key}'", keyStr);
                    continue;
                }

                var mods = KeyModifiers.None;
                if (item["modifiers"]?.AsArray() is { } modsNode)
                {
                    foreach (var mod in modsNode)
                    {
                        mods |= mod?.GetValue<string>() switch
                        {
                            "Ctrl"  => KeyModifiers.Control,
                            "Alt"   => KeyModifiers.Alt,
                            "Shift" => KeyModifiers.Shift,
                            _       => KeyModifiers.None
                        };
                    }
                }

                _bindings[new HotkeyKey(key, mods)] = action;
            }

            _logger.LogInformation("Loaded {Count} hotkey bindings from '{Path}'",
                _bindings.Count, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load hotkeys.json from '{Path}'", path);
        }
    }

    public bool HandleKeyDown(Key key, KeyModifiers modifiers)
    {
        if (!_bindings.TryGetValue(new HotkeyKey(key, modifiers), out var action))
            return false;

        ActionTriggered?.Invoke(action);
        return true;
    }

    private readonly record struct HotkeyKey(Key Key, KeyModifiers Modifiers);
}

/// <summary>
/// String constants for all abstract action tokens used in hotkeys.json.
/// ViewModels switch on these values in their ActionTriggered handlers.
/// </summary>
public static class HotkeyActions
{
    // ── Playback ──────────────────────────────────────────────────────────────
    public const string PlaybackPlayStopToggle = "PLAYBACK_PLAY_STOP_TOGGLE";
    public const string PlaybackStop           = "PLAYBACK_STOP";
    public const string PlaybackNext           = "PLAYBACK_NEXT";

    // ── Windows ───────────────────────────────────────────────────────────────
    public const string WindowTracksManager    = "WINDOW_TRACKS_MANAGER";
    public const string WindowTrackEditor      = "WINDOW_TRACK_EDITOR";
    public const string WindowPlaylistBuilder  = "WINDOW_PLAYLIST_BUILDER";
    public const string WindowScheduledEvents  = "WINDOW_SCHEDULED_EVENTS";

    // ── Cartwall ──────────────────────────────────────────────────────────────
    public const string CartwallToggleMode     = "CARTWALL_TOGGLE_MODE";
    public const string CartwallTab1           = "CARTWALL_TAB_1";
    public const string CartwallTab2           = "CARTWALL_TAB_2";
    public const string CartwallTab3           = "CARTWALL_TAB_3";
    public const string CartwallTab4           = "CARTWALL_TAB_4";
    public const string CartwallTab5           = "CARTWALL_TAB_5";
    public const string CartwallTab6           = "CARTWALL_TAB_6";
    public const string CartwallTab7           = "CARTWALL_TAB_7";
    public const string CartwallTriggerSlot1   = "CARTWALL_TRIGGER_SLOT_1";
    public const string CartwallTriggerSlot2   = "CARTWALL_TRIGGER_SLOT_2";
    public const string CartwallTriggerSlot3   = "CARTWALL_TRIGGER_SLOT_3";
    public const string CartwallTriggerSlot4   = "CARTWALL_TRIGGER_SLOT_4";
    public const string CartwallTriggerSlot5   = "CARTWALL_TRIGGER_SLOT_5";
    public const string CartwallTriggerSlot6   = "CARTWALL_TRIGGER_SLOT_6";
    public const string CartwallTriggerSlot7   = "CARTWALL_TRIGGER_SLOT_7";
    public const string CartwallTriggerSlot8   = "CARTWALL_TRIGGER_SLOT_8";
    public const string CartwallTriggerSlot9   = "CARTWALL_TRIGGER_SLOT_9";

    // ── Playlist ──────────────────────────────────────────────────────────────
    public const string PlaylistRemoveSelected = "PLAYLIST_REMOVE_SELECTED";

    // ── Microphone ────────────────────────────────────────────────────────────
    public const string MicToggle = "MIC_TOGGLE";

    // ── Editors ───────────────────────────────────────────────────────────────
    public const string Save = "SAVE";
    public const string Undo = "UNDO";
    public const string Redo = "REDO";
}
