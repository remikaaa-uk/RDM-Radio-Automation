using Avalonia;
using Avalonia.Media;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace RDM.UI.Services;

/// <summary>
/// Manages two responsibilities:
/// 1. Theme: loads theme.json at startup and injects colors into Application.Current.Resources
///    as DynamicResource keys so all views pick up changes via {DynamicResource} bindings.
/// 2. UI State: reads and writes window positions, last-used tab indices, and filter presets
///    to the ui_state section of rdm.config.json.
///
/// LoadThemeAsync() MUST be called on the UI thread before any window is shown.
/// </summary>
public sealed class UiStateService
{
    private const string ThemeFile  = "theme.json";
    private static readonly string ConfigFile = ConfigPaths.FilePath;

    private readonly ILogger<UiStateService> _logger;

    public UiStateService(ILogger<UiStateService> logger) => _logger = logger;

    // ── Theme ─────────────────────────────────────────────────────────────────

    /// Parses theme.json and injects all color resources into Application.Current.Resources.
    /// Must run on the UI thread.
    public async Task LoadThemeAsync()
    {
        if (!File.Exists(ThemeFile))
        {
            _logger.LogInformation("theme.json not found — using compiled defaults");
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(ThemeFile);
            var root = JsonNode.Parse(json);
            if (root is null) return;

            InjectBrushes(root);
            _logger.LogInformation("Theme loaded from theme.json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load theme.json — using compiled defaults");
        }
    }

    private static void InjectBrushes(JsonNode root)
    {
        if (Application.Current is null) return;

        static void Set(string key, string? hex)
        {
            if (hex is null) return;
            try
            {
                Application.Current!.Resources[key] =
                    new SolidColorBrush(Color.Parse(hex));
            }
            catch { /* invalid hex — keep compiled default */ }
        }

        var pl = root["playlist"];
        if (pl is not null)
        {
            Set("PlaylistTrackBackground",    pl["track_background"]?.GetValue<string>());
            Set("PlaylistDummyBackground",    pl["dummy_background"]?.GetValue<string>());
            Set("PlaylistSweeperBackground",  pl["sweeper_background"]?.GetValue<string>());
            Set("PlaylistCartBackground",     pl["cart_background"]?.GetValue<string>());
            Set("PlaylistPlayingBorderBrush", pl["playing_border"]?.GetValue<string>());
            Set("PlaylistNextBackground",     pl["next_background"]?.GetValue<string>());
            Set("PlaylistEtaBrush",           pl["eta_brush"]?.GetValue<string>());
            Set("PlaylistEtaStoppedBrush",    pl["eta_stopped_brush"]?.GetValue<string>());
            Set("PlaylistIntroBrush",         pl["intro_brush"]?.GetValue<string>());
            Set("PlaylistOutroBrush",         pl["outro_brush"]?.GetValue<string>());
        }

        var player = root["player"];
        if (player is not null)
        {
            Set("PlayerConnectedBrush",    player["connected_foreground"]?.GetValue<string>());
            Set("PlayerDisconnectedBrush", player["disconnected_foreground"]?.GetValue<string>());
        }

        var wf = root["waveform"];
        if (wf is not null)
        {
            Set("WaveformIntroZoneBrush",    wf["intro_zone"]?.GetValue<string>());
            Set("WaveformVocalZoneBrush",    wf["vocal_zone"]?.GetValue<string>());
            Set("WaveformOutroZoneBrush",    wf["outro_zone"]?.GetValue<string>());
            Set("WaveformUnplayedBrush",     wf["unplayed"]?.GetValue<string>());
            Set("WaveformMarkerIntroBrush",  wf["marker_intro"]?.GetValue<string>());
            Set("WaveformMarkerOutroBrush",  wf["marker_outro"]?.GetValue<string>());
            Set("WaveformMarkerHookBrush",   wf["marker_hook"]?.GetValue<string>());
            Set("WaveformMarkerCustomBrush", wf["marker_custom"]?.GetValue<string>());
        }

        var formats = root["formats"];
        if (formats is not null)
        {
            foreach (var prop in formats.AsObject())
                Set("PlaylistFormat_" + prop.Key, prop.Value?.GetValue<string>());
        }
    }

    // ── UI State persistence ──────────────────────────────────────────────────

    /// Returns the ui_state JsonNode (or null if absent/unreadable).
    public async Task<JsonNode?> LoadUiStateAsync()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return null;
            var json = await File.ReadAllTextAsync(ConfigFile);
            return JsonNode.Parse(json)?["ui_state"];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read ui_state from rdm.config.json");
            return null;
        }
    }

    public async Task SaveWindowPositionAsync(
        string windowKey, double x, double y, double width, double height)
    {
        await UpdateConfigAsync(cfg =>
        {
            EnsureUiState(cfg);
            cfg["ui_state"]![windowKey] = new JsonObject
            {
                ["x"]      = x,
                ["y"]      = y,
                ["width"]  = width,
                ["height"] = height
            };
        });
    }

    public async Task SaveTabIndexAsync(string panelKey, int index)
    {
        await UpdateConfigAsync(cfg =>
        {
            EnsureUiState(cfg);
            cfg["ui_state"]![panelKey + "_tab"] = index;
        });
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void EnsureUiState(JsonObject cfg)
        => cfg["ui_state"] ??= new JsonObject();

    private async Task UpdateConfigAsync(Action<JsonObject> mutate)
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;
            var json    = await File.ReadAllTextAsync(ConfigFile);
            var root    = (JsonNode.Parse(json) as JsonObject) ?? new JsonObject();
            mutate(root);
            await File.WriteAllTextAsync(ConfigFile,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save ui_state to rdm.config.json");
        }
    }
}
