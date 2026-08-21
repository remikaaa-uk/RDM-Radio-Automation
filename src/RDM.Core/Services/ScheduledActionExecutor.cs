using System.Text.Json;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Shared.Enums;

namespace RDM.Core.Services;

/// <summary>
/// Executes the ordered action list of a scheduled event. One action per array element,
/// run sequentially — a WAIT action pauses the sequence, letting operators compose flows like
/// "clear → add tracks → stop → wait 1s → play".
///
/// Actions are grouped by concern:
///   • PLAYBACK  — PLAY, PAUSE, STOP, RESET, NEXT
///   • QUEUE     — CLEAR_PLAYLIST, LOAD_PLAYLIST, ADD_ITEM, ADD_EXTERNAL_ITEM,
///                 REMOVE_ITEM, REMOVE_CURRENT_ITEM, REORDER_ITEM
///   • ITEM      — PATCH_ITEM
///   • MODE      — CHANGE_PLAYLIST_MODE
///   • SWEEPER   — CHANGE_SWEEPER_CATEGORY, CHANGE_SWEEPER_SUBCATEGORY
///   • CONTROL   — WAIT
///   • EXTERNAL  — EXECUTE_FILE, HTTP_CALL   (gated by IExternalActionRunner)
///
/// Split out of EventScheduler so scheduling (when to fire) stays separate from
/// execution (what to run), per SRP.
/// </summary>
public sealed class ScheduledActionExecutor
{
    private readonly IPlaylistController          _playlist;
    private readonly IAudioSettingsRepository     _settingsRepo;
    private readonly IAssetFormatRepository       _formatRepo;
    private readonly ISubcategoryRepository       _subcategoryRepo;
    private readonly IExternalActionRunner        _external;
    private readonly StudioContext                _studioContext;
    private readonly ILogger<ScheduledActionExecutor> _logger;

    public ScheduledActionExecutor(
        IPlaylistController               playlist,
        IAudioSettingsRepository          settingsRepo,
        IAssetFormatRepository            formatRepo,
        ISubcategoryRepository            subcategoryRepo,
        IExternalActionRunner             external,
        StudioContext                     studioContext,
        ILogger<ScheduledActionExecutor>  logger)
    {
        _playlist        = playlist;
        _settingsRepo    = settingsRepo;
        _formatRepo      = formatRepo;
        _subcategoryRepo = subcategoryRepo;
        _external        = external;
        _studioContext   = studioContext;
        _logger          = logger;
    }

    /// <summary>Parses <paramref name="actionsJson"/> (a JSON array of {type, payload}) and runs each action in order.</summary>
    public async Task ExecuteAsync(string actionsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actionsJson))
            return;

        using var doc = JsonDocument.Parse(actionsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var action in doc.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            // Wrapper keys may be "type"/"payload" (API camelCase) or "Type"/"Payload"
            // (records serialized with default options) — read case-insensitively.
            var type = TryGetProp(action, "type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? string.Empty
                : string.Empty;
            TryGetProp(action, "payload", out var payload);
            await ExecuteOneAsync(type, payload, ct);
        }
    }

    /// <summary>Case-insensitive property lookup (JsonElement.TryGetProperty is case-sensitive).</summary>
    private static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private async Task ExecuteOneAsync(string type, JsonElement payload, CancellationToken ct)
    {
        switch (type.ToUpperInvariant())
        {
            // ── PLAYBACK ────────────────────────────────────────────────────────
            case "PLAY":
                await _playlist.PlayAsync(ct);
                break;
            case "PAUSE":
                await _playlist.PauseAsync(ct);
                break;
            case "STOP":
                await _playlist.StopAsync(ct);
                break;
            case "RESET":
                await _playlist.ResetAsync(ct);
                break;
            case "NEXT":
                await _playlist.NextTrackAsync(ct);
                break;
            case "PLAY_FILE":
                await _playlist.PlayAssetDirectlyAsync(RequireString(payload, "asset_id"), ct);
                break;

            // ── QUEUE MANAGEMENT ────────────────────────────────────────────────
            case "CLEAR_PLAYLIST":
                _logger.LogInformation("Scheduled action CLEAR_PLAYLIST executing");
                await _playlist.ClearAsync(ct);
                break;
            case "LOAD_PLAYLIST":
                var loadPlaylistId = RequireString(payload, "playlist_id");
                _logger.LogInformation("Scheduled action LOAD_PLAYLIST executing (playlist_id={PlaylistId})", loadPlaylistId);
                await _playlist.LoadSavedPlaylistIntoQueueAsync(loadPlaylistId, ct);
                break;
            case "ADD_ITEM":
                await _playlist.AddItemAsync(
                    RequireString(payload, "asset_id"),
                    OptionalInt(payload, "position") ?? -1, ct);
                break;
            case "ADD_EXTERNAL_ITEM":
                await _playlist.AddExternalItemAsync(
                    RequireString(payload, "file_path"),
                    OptionalString(payload, "title"),
                    OptionalString(payload, "artist"),
                    (uint?)OptionalInt(payload, "duration_ms"),
                    OptionalInt(payload, "position") ?? -1, ct);
                break;
            case "REMOVE_ITEM":
                await _playlist.RemoveItemAsync(RequireString(payload, "item_id"), ct);
                break;
            case "REMOVE_CURRENT_ITEM":
                await _playlist.RemoveCurrentItemAsync(ct);
                break;
            case "REORDER_ITEM":
                await _playlist.ReorderItemAsync(
                    RequireString(payload, "item_id"),
                    OptionalInt(payload, "new_position") ?? 0, ct);
                break;

            // ── ITEM PROPERTIES ─────────────────────────────────────────────────
            case "PATCH_ITEM":
                await _playlist.PatchItemAsync(
                    RequireString(payload, "item_id"),
                    (uint?)OptionalInt(payload, "crossfade_ms"),
                    OptionalInt(payload, "lead_in_ms"),
                    (uint?)OptionalInt(payload, "trim_start_ms"),
                    (uint?)OptionalInt(payload, "trim_end_ms"),
                    OptionalString(payload, "segue_type"),
                    OptionalBool(payload, "auto_link_next"),
                    OptionalString(payload, "volume_envelope"),
                    ct);
                break;

            // ── PLAYLIST MODE ───────────────────────────────────────────────────
            case "CHANGE_PLAYLIST_MODE":
                await _playlist.ChangeModeAsync(ParseMode(RequireString(payload, "mode")), ct);
                break;

            // ── SWEEPER ─────────────────────────────────────────────────────────
            case "CHANGE_SWEEPER_CATEGORY":
                await ChangeSweeperCategoryAsync(RequireString(payload, "format_name"), ct);
                break;
            case "CHANGE_SWEEPER_SUBCATEGORY":
                await ChangeSweeperSubcategoryAsync(OptionalString(payload, "subcategory_name"), ct);
                break;

            // ── CONTROL FLOW ────────────────────────────────────────────────────
            case "WAIT":
                var ms = OptionalInt(payload, "duration_ms") ?? 0;
                if (ms > 0)
                {
                    _logger.LogDebug("WAIT {Ms}ms", ms);
                    await Task.Delay(ms, ct);
                }
                break;

            // ── EXTERNAL ────────────────────────────────────────────────────────
            case "EXECUTE_FILE":
            {
                var result = await _external.RunFileAsync(
                    RequireString(payload, "path"),
                    OptionalString(payload, "arguments"),
                    OptionalString(payload, "working_dir"),
                    OptionalInt(payload, "timeout_ms") ?? 30_000,
                    OptionalBool(payload, "capture_output") ?? false,
                    ct);
                if (!result.Success)
                    _logger.LogWarning("EXECUTE_FILE failed (exit {Code}): {Error}", result.ExitCode, result.Error);
                break;
            }
            case "HTTP_CALL":
            {
                var result = await _external.RunHttpAsync(
                    OptionalString(payload, "method") ?? "GET",
                    RequireString(payload, "url"),
                    OptionalString(payload, "body"),
                    ReadHeaders(payload),
                    OptionalInt(payload, "timeout_ms") ?? 30_000,
                    ct);
                if (!result.Success)
                    _logger.LogWarning("HTTP_CALL failed (status {Code}): {Error}", result.ExitCode, result.Error);
                break;
            }

            default:
                _logger.LogWarning("Unknown scheduled action type '{Type}' — skipped", type);
                break;
        }
    }

    // ── Sweeper ─────────────────────────────────────────────────────────────────

    private async Task ChangeSweeperCategoryAsync(string formatName, CancellationToken ct)
    {
        var settings = await _settingsRepo.GetByStudioAsync(_studioContext.StudioId, ct);
        if (settings is null)
        {
            _logger.LogWarning("CHANGE_SWEEPER_CATEGORY: audio_settings not found");
            return;
        }

        var formats = await _formatRepo.GetAllAsync(ct);
        var match = formats.FirstOrDefault(
            f => string.Equals(f.Name, formatName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            _logger.LogWarning(
                "CHANGE_SWEEPER_CATEGORY '{Name}': no matching format — action skipped", formatName);
            return;
        }

        // Changing the format resets the active subcategory: the previously selected subcategory
        // belongs to the old format and would filter the new pool to nothing.
        await _settingsRepo.UpdateAsync(
            settings with { SweeperFormatId = match.FormatId, SweeperSubcategoryId = null }, ct);

        _logger.LogInformation(
            "CHANGE_SWEEPER_CATEGORY: sweeper format set to '{Name}' ({FormatId})",
            match.Name, match.FormatId);
    }

    /// <summary>
    /// Sets the active sweeper subcategory (pool filter). An empty/omitted name clears it to null
    /// (= randomize across the whole sweeper category). Otherwise the name is resolved against the
    /// currently active sweeper format's subcategories.
    /// </summary>
    private async Task ChangeSweeperSubcategoryAsync(string? subcategoryName, CancellationToken ct)
    {
        var settings = await _settingsRepo.GetByStudioAsync(_studioContext.StudioId, ct);
        if (settings is null)
        {
            _logger.LogWarning("CHANGE_SWEEPER_SUBCATEGORY: audio_settings not found");
            return;
        }

        // Empty name = clear to "all" (whole category).
        if (string.IsNullOrWhiteSpace(subcategoryName))
        {
            await _settingsRepo.UpdateAsync(settings with { SweeperSubcategoryId = null }, ct);
            _logger.LogInformation("CHANGE_SWEEPER_SUBCATEGORY: cleared (randomize across whole category)");
            return;
        }

        if (settings.SweeperFormatId is null)
        {
            _logger.LogWarning(
                "CHANGE_SWEEPER_SUBCATEGORY '{Name}': no active sweeper format — action skipped", subcategoryName);
            return;
        }

        var subcategories = await _subcategoryRepo.GetByFormatIdAsync(settings.SweeperFormatId, ct);
        var match = subcategories.FirstOrDefault(
            s => string.Equals(s.Name, subcategoryName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            _logger.LogWarning(
                "CHANGE_SWEEPER_SUBCATEGORY '{Name}': no matching subcategory in active sweeper format — action skipped",
                subcategoryName);
            return;
        }

        await _settingsRepo.UpdateAsync(settings with { SweeperSubcategoryId = match.SubcategoryId }, ct);
        _logger.LogInformation(
            "CHANGE_SWEEPER_SUBCATEGORY: sweeper subcategory set to '{Name}' ({SubId})",
            match.Name, match.SubcategoryId);
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static PlaylistMode ParseMode(string modeStr) => modeStr.ToUpperInvariant() switch
    {
        "AUTO"        => PlaylistMode.Auto,
        "LIVE_ASSIST" => PlaylistMode.LiveAssist,
        "MANUAL"      => PlaylistMode.Manual,
        _             => throw new ArgumentException($"Unknown playlist mode: {modeStr}")
    };

    private static string RequireString(JsonElement payload, string name)
    {
        if (TryGetProp(payload, name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            var value = el.GetString();
            if (!string.IsNullOrEmpty(value))
                return value;
        }
        throw new ArgumentException($"Required action field '{name}' is missing.");
    }

    private static string? OptionalString(JsonElement payload, string name)
        => TryGetProp(payload, name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int? OptionalInt(JsonElement payload, string name)
    {
        if (!TryGetProp(payload, name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var n)              => n,
            JsonValueKind.String when int.TryParse(el.GetString(), out var s) => s,
            _                                                                 => null
        };
    }

    private static bool? OptionalBool(JsonElement payload, string name)
    {
        if (!TryGetProp(payload, name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            _                   => null
        };
    }

    private static IReadOnlyDictionary<string, string>? ReadHeaders(JsonElement payload)
    {
        if (!TryGetProp(payload, "headers", out var el) || el.ValueKind != JsonValueKind.Object)
            return null;

        var dict = new Dictionary<string, string>();
        foreach (var prop in el.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.String)
                dict[prop.Name] = prop.Value.GetString() ?? string.Empty;

        return dict.Count > 0 ? dict : null;
    }
}
