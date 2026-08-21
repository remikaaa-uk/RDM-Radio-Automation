using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

// ── Row VM (list display) ─────────────────────────────────────────────────────

public sealed class ScheduledEventRowViewModel
{
    public ScheduledEventDto Source { get; }

    public string  EventId      => Source.EventId;
    public string  Name         => Source.Name;
    public string  EventType    => Source.EventType;
    public string  Category     => Source.Category;
    public bool    Enabled      => Source.Enabled;
    public bool    SkipNext     => Source.SkipNext;

    public string  EnabledText  { get; }
    public string  EnabledColor { get; }
    public string  SkipNextText { get; }
    public string  DaysText     { get; }
    public string  HoursText    { get; }
    public string  LastFiredText{ get; }

    public ScheduledEventRowViewModel(ScheduledEventDto dto)
    {
        Source       = dto;
        EnabledText  = dto.Enabled
            ? Localizer.Instance?["se.row.enabled"]  ?? "ACTIVE"
            : Localizer.Instance?["se.row.disabled"] ?? "DISABLED";
        EnabledColor = dto.Enabled ? "#5DDC8A" : "#888899";
        SkipNextText = dto.SkipNext ? Localizer.Instance?["se.row.skip"] ?? "⏭ SKIP" : "";

        DaysText = dto.Days.Count == 0
            ? "—"
            : string.Join(" ", dto.Days.Select(d => d switch
            {
                "MON" => Localizer.Instance?["se.day.mon"] ?? "Mon",
                "TUE" => Localizer.Instance?["se.day.tue"] ?? "Tue",
                "WED" => Localizer.Instance?["se.day.wed"] ?? "Wed",
                "THU" => Localizer.Instance?["se.day.thu"] ?? "Thu",
                "FRI" => Localizer.Instance?["se.day.fri"] ?? "Fri",
                "SAT" => Localizer.Instance?["se.day.sat"] ?? "Sat",
                "SUN" => Localizer.Instance?["se.day.sun"] ?? "Sun",
                _     => d
            }));

        HoursText = dto.Hours.Count == 0
            ? (dto.EventHour ?? "—")
            : string.Join(", ", dto.Hours.Select(h =>
            {
                if (!string.IsNullOrEmpty(dto.EventHour) &&
                    TimeSpan.TryParse(dto.EventHour, out var eh))
                    return $"{h:D2}:{eh.Minutes:D2}:{eh.Seconds:D2}";
                return $"{h:D2}:00:00";
            }));

        // LastFiredAt is persisted in local time (scheduler uses DateTime.Now) → display as-is.
        LastFiredText = dto.LastFiredAt.HasValue
            ? dto.LastFiredAt.Value.ToString("dd.MM HH:mm")
            : "—";
    }
}

// ── Action catalog (grouped) ───────────────────────────────────────────────────

/// <summary>
/// A single selectable action type. Only the type key is stored — <see cref="Label"/> resolves
/// through the localizer on access, because <see cref="ActionCatalog.Groups"/> is static and would
/// otherwise freeze the labels at the language active when the type was first loaded.
/// </summary>
public sealed record ActionTypeOption(string Type)
{
    public string Label => ActionCatalog.TypeLabel(Type);
    public override string ToString() => Label;
}

/// <summary>A named group of related action types shown in the group dropdown.</summary>
public sealed record ActionGroupOption(string Key, IReadOnlyList<ActionTypeOption> Types)
{
    public string Name => ActionCatalog.GroupLabel(Key);
    public override string ToString() => Name;
}

/// <summary>A saved playlist from the database, shown by name in the LOAD_PLAYLIST picker.</summary>
public sealed record PlaylistOption(string PlaylistId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Static catalog of every action the scheduler can execute, grouped by concern.</summary>
public static class ActionCatalog
{
    public static IReadOnlyList<ActionGroupOption> Groups { get; } =
    [
        new("playback",
        [
            new("PLAY"), new("PAUSE"), new("STOP"),
            new("RESET"), new("NEXT"), new("PLAY_FILE"),
        ]),
        new("queue",
        [
            new("CLEAR_PLAYLIST"), new("LOAD_PLAYLIST"), new("ADD_ITEM"),
            new("ADD_EXTERNAL_ITEM"), new("REMOVE_ITEM"), new("REMOVE_CURRENT_ITEM"),
            new("REORDER_ITEM"),
        ]),
        new("item_props",  [ new("PATCH_ITEM") ]),
        new("playlist_mode", [ new("CHANGE_PLAYLIST_MODE") ]),
        new("sweeper",
        [
            new("CHANGE_SWEEPER_CATEGORY"), new("CHANGE_SWEEPER_SUBCATEGORY"),
        ]),
        new("control",  [ new("WAIT") ]),
        new("external", [ new("EXECUTE_FILE"), new("HTTP_CALL") ]),
    ];

    internal static string GroupLabel(string key) => key switch
    {
        "playback"      => Tr("se.act.grp.playback",      "Playback"),
        "queue"         => Tr("se.act.grp.queue",         "Queue"),
        "item_props"    => Tr("se.act.grp.item_props",    "Item properties"),
        "playlist_mode" => Tr("se.act.grp.playlist_mode", "Playlist mode"),
        "sweeper"       => Tr("se.act.grp.sweeper",       "Sweeper"),
        "control"       => Tr("se.act.grp.control",       "Control"),
        "external"      => Tr("se.act.grp.external",      "External"),
        _               => key
    };

    internal static string TypeLabel(string type) => type switch
    {
        "PLAY"                       => Tr("se.act.play",                       "Play"),
        "PAUSE"                      => Tr("se.act.pause",                      "Pause"),
        "STOP"                       => Tr("se.act.stop",                       "Stop"),
        "RESET"                      => Tr("se.act.reset",                      "Reset to start"),
        "NEXT"                       => Tr("se.act.next",                       "Next track"),
        "PLAY_FILE"                  => Tr("se.act.play_file",                  "Play asset immediately"),
        "CLEAR_PLAYLIST"             => Tr("se.act.clear_playlist",             "Clear queue"),
        "LOAD_PLAYLIST"              => Tr("se.act.load_playlist",              "Load playlist from database"),
        "ADD_ITEM"                   => Tr("se.act.add_item",                   "Add asset from library"),
        "ADD_EXTERNAL_ITEM"          => Tr("se.act.add_external_item",          "Add external file"),
        "REMOVE_ITEM"                => Tr("se.act.remove_item",                "Remove item"),
        "REMOVE_CURRENT_ITEM"        => Tr("se.act.remove_current_item",        "Remove current item"),
        "REORDER_ITEM"               => Tr("se.act.reorder_item",               "Move item"),
        "PATCH_ITEM"                 => Tr("se.act.patch_item",                 "Change item parameters"),
        "CHANGE_PLAYLIST_MODE"       => Tr("se.act.change_playlist_mode",       "Change mode (Auto/Live/Manual)"),
        "CHANGE_SWEEPER_CATEGORY"    => Tr("se.act.change_sweeper_category",    "Change sweeper category"),
        "CHANGE_SWEEPER_SUBCATEGORY" => Tr("se.act.change_sweeper_subcategory", "Change sweeper subcategory"),
        "WAIT"                       => Tr("se.act.wait",                       "Wait"),
        "EXECUTE_FILE"               => Tr("se.act.execute_file",               "Run file / script"),
        "HTTP_CALL"                  => Tr("se.act.http_call",                  "HTTP request"),
        _                            => type
    };

    private static string Tr(string key, string fallback) => Localizer.Instance?[key] ?? fallback;

    public static ActionGroupOption GroupForType(string type) =>
        Groups.FirstOrDefault(g => g.Types.Any(t => t.Type == type)) ?? Groups[0];

    public static ActionTypeOption OptionForType(string type) =>
        Groups.SelectMany(g => g.Types).FirstOrDefault(t => t.Type == type)
        ?? Groups[0].Types[0];
}

// ── Action editor VM ──────────────────────────────────────────────────────────

public sealed partial class ActionItemViewModel : ObservableObject
{
    public static IReadOnlyList<ActionGroupOption> Groups { get; } = ActionCatalog.Groups;

    public static IReadOnlyList<string> PlaylistModes { get; } = ["AUTO", "LIVE_ASSIST", "MANUAL"];
    public static IReadOnlyList<string> HttpMethods   { get; } = ["GET", "POST", "PUT", "DELETE", "PATCH"];
    public static IReadOnlyList<string> SegueTypes    { get; } = ["AUTO", "CROSSFADE", "CUT", "GAP"];

    /// <summary>Saved playlists from the database, for the LOAD_PLAYLIST picker (populated by the parent VM).</summary>
    public static ObservableCollection<PlaylistOption> AvailablePlaylists { get; } = new();

    /// <summary>Set by the window; opens the library asset picker dialog (search + filter) and returns the chosen asset, or null if cancelled.</summary>
    public static Func<Task<AssetPickerRow?>>? ShowAssetPicker { get; set; }

    // Position in the sequence (1-based), maintained by the parent VM.
    [ObservableProperty] private int _index;

    // ── Group / type selection ─────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeOptions))]
    private ActionGroupOption _selectedGroup = ActionCatalog.Groups[0];

    public IReadOnlyList<ActionTypeOption> TypeOptions => SelectedGroup.Types;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(IsMode))]
    [NotifyPropertyChangedFor(nameof(IsCategory))]
    [NotifyPropertyChangedFor(nameof(IsSweeperSubcategory))]
    [NotifyPropertyChangedFor(nameof(IsAsset))]
    [NotifyPropertyChangedFor(nameof(IsPosition))]
    [NotifyPropertyChangedFor(nameof(IsExternalItem))]
    [NotifyPropertyChangedFor(nameof(IsPlaylist))]
    [NotifyPropertyChangedFor(nameof(IsItemId))]
    [NotifyPropertyChangedFor(nameof(IsNewPosition))]
    [NotifyPropertyChangedFor(nameof(IsPatch))]
    [NotifyPropertyChangedFor(nameof(IsWait))]
    [NotifyPropertyChangedFor(nameof(IsExecuteFile))]
    [NotifyPropertyChangedFor(nameof(IsHttp))]
    [NotifyPropertyChangedFor(nameof(HasParameters))]
    private ActionTypeOption _selectedType = ActionCatalog.Groups[0].Types[0];

    partial void OnSelectedGroupChanged(ActionGroupOption value)
    {
        // Keep the type valid for the newly-picked group.
        if (!value.Types.Contains(SelectedType))
            SelectedType = value.Types[0];
    }

    public string ActionType => SelectedType.Type;

    // ── Parameter fields ────────────────────────────────────────────────────────

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _playlistMode = "AUTO";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _categoryName = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _sweeperSubcategoryName = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] [NotifyPropertyChangedFor(nameof(AssetDisplayText))] private string _assetId = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] [NotifyPropertyChangedFor(nameof(AssetDisplayText))] private string _assetDisplayName = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _position = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _filePath = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _playlistId = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _itemId = "";
    [ObservableProperty] private string _newPosition = "";

    /// <summary>Playlist picked by name in the LOAD_PLAYLIST editor — mirrors <see cref="PlaylistId"/>.</summary>
    public PlaylistOption? SelectedPlaylist
    {
        get => AvailablePlaylists.FirstOrDefault(p => p.PlaylistId == PlaylistId);
        set => PlaylistId = value?.PlaylistId ?? "";
    }

    partial void OnPlaylistIdChanged(string value) => OnPropertyChanged(nameof(SelectedPlaylist));

    /// <summary>Re-raises SelectedPlaylist change once AvailablePlaylists finishes (re-)loading.</summary>
    public void RefreshPlaylistSelection() => OnPropertyChanged(nameof(SelectedPlaylist));

    /// <summary>Friendly label for the asset slot — the name/artist picked via the dialog, or the raw ID for actions loaded before a name was cached.</summary>
    public string AssetDisplayText =>
        string.IsNullOrWhiteSpace(AssetDisplayName)
            ? (string.IsNullOrWhiteSpace(AssetId) ? "— nie wybrano —" : AssetId)
            : AssetDisplayName;

    [RelayCommand]
    private async Task BrowseAssetAsync()
    {
        if (ShowAssetPicker is null) return;
        var picked = await ShowAssetPicker();
        if (picked is null) return;
        AssetId          = picked.AssetId;
        AssetDisplayName = picked.HasArtist ? $"{picked.Title} — {picked.Artist}" : picked.Title;
    }

    // PATCH_ITEM fields
    [ObservableProperty] private string _crossfadeMs = "";
    [ObservableProperty] private string _leadInMs = "";
    [ObservableProperty] private string _trimStartMs = "";
    [ObservableProperty] private string _trimEndMs = "";
    [ObservableProperty] private string _segueType = "AUTO";
    [ObservableProperty] private bool   _autoLinkNext;

    // WAIT
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _waitMs = "1000";

    // EXECUTE_FILE
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _execPath = "";
    [ObservableProperty] private string _execArguments = "";
    [ObservableProperty] private string _execWorkingDir = "";
    [ObservableProperty] private string _execTimeoutMs = "30000";
    [ObservableProperty] private bool   _execCaptureOutput;

    // HTTP_CALL
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _httpMethod = "GET";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _httpUrl = "";
    [ObservableProperty] private string _httpBody = "";
    [ObservableProperty] private string _httpTimeoutMs = "30000";

    // ── Visibility flags ──────────────────────────────────────────────────────

    public bool IsMode              => ActionType == "CHANGE_PLAYLIST_MODE";
    public bool IsCategory          => ActionType == "CHANGE_SWEEPER_CATEGORY";
    public bool IsSweeperSubcategory => ActionType == "CHANGE_SWEEPER_SUBCATEGORY";
    public bool IsAsset        => ActionType is "ADD_ITEM" or "PLAY_FILE";
    public bool IsPosition     => ActionType is "ADD_ITEM" or "ADD_EXTERNAL_ITEM";
    public bool IsExternalItem => ActionType == "ADD_EXTERNAL_ITEM";
    public bool IsPlaylist     => ActionType == "LOAD_PLAYLIST";
    public bool IsItemId       => ActionType is "REMOVE_ITEM" or "REORDER_ITEM" or "PATCH_ITEM";
    public bool IsNewPosition  => ActionType == "REORDER_ITEM";
    public bool IsPatch        => ActionType == "PATCH_ITEM";
    public bool IsWait         => ActionType == "WAIT";
    public bool IsExecuteFile  => ActionType == "EXECUTE_FILE";
    public bool IsHttp         => ActionType == "HTTP_CALL";

    public bool HasParameters =>
        IsMode || IsCategory || IsSweeperSubcategory || IsAsset || IsPosition || IsPlaylist ||
        IsItemId || IsPatch || IsWait || IsExecuteFile || IsHttp;

    /// <summary>One-line human summary shown in the sequence list header.</summary>
    public string Summary
    {
        get
        {
            var detail = ActionType switch
            {
                "CHANGE_PLAYLIST_MODE"  => PlaylistMode,
                "CHANGE_SWEEPER_CATEGORY" => CategoryName,
                "CHANGE_SWEEPER_SUBCATEGORY" => string.IsNullOrWhiteSpace(SweeperSubcategoryName)
                                                   ? "wszystkie" : SweeperSubcategoryName,
                "ADD_ITEM"              => $"{AssetDisplayText}{(string.IsNullOrWhiteSpace(Position) ? "" : $" @ {Position}")}",
                "PLAY_FILE"             => AssetDisplayText,
                "ADD_EXTERNAL_ITEM"     => System.IO.Path.GetFileName(FilePath),
                "LOAD_PLAYLIST"         => SelectedPlaylist?.Name ?? PlaylistId,
                "REMOVE_ITEM"           => ItemId,
                "REORDER_ITEM"          => $"{ItemId} → {NewPosition}",
                "PATCH_ITEM"            => ItemId,
                "WAIT"                  => $"{WaitMs} ms",
                "EXECUTE_FILE"          => System.IO.Path.GetFileName(ExecPath),
                "HTTP_CALL"             => $"{HttpMethod} {HttpUrl}",
                _                       => ""
            };
            return string.IsNullOrWhiteSpace(detail)
                ? SelectedType.Label
                : $"{SelectedType.Label} — {detail}";
        }
    }

    // ── Serialization ─────────────────────────────────────────────────────────

    public ScheduledEventActionDto ToDto()
    {
        object payload = ActionType switch
        {
            "CHANGE_PLAYLIST_MODE"    => new { mode = PlaylistMode },
            "CHANGE_SWEEPER_CATEGORY" => new { format_name = CategoryName },
            "CHANGE_SWEEPER_SUBCATEGORY" => new { subcategory_name = SweeperSubcategoryName },
            "PLAY_FILE"               => new { asset_id = AssetId },
            "ADD_ITEM"                => BuildAddItem(),
            "ADD_EXTERNAL_ITEM"       => BuildAddExternal(),
            "LOAD_PLAYLIST"           => new { playlist_id = PlaylistId },
            "REMOVE_ITEM"             => new { item_id = ItemId },
            "REORDER_ITEM"            => new { item_id = ItemId, new_position = ParseInt(NewPosition) ?? 0 },
            "PATCH_ITEM"              => BuildPatch(),
            "WAIT"                    => new { duration_ms = ParseInt(WaitMs) ?? 0 },
            "EXECUTE_FILE"            => BuildExecute(),
            "HTTP_CALL"               => BuildHttp(),
            _                         => new { }
        };
        return new ScheduledEventActionDto(ActionType, payload);
    }

    private object BuildAddItem()
    {
        var pos = ParseInt(Position);
        return pos.HasValue ? new { asset_id = AssetId, position = pos.Value } : new { asset_id = AssetId };
    }

    private object BuildAddExternal()
    {
        var dict = new Dictionary<string, object> { ["file_path"] = FilePath };
        if (!string.IsNullOrWhiteSpace(Title))  dict["title"]  = Title;
        if (!string.IsNullOrWhiteSpace(Artist)) dict["artist"] = Artist;
        var pos = ParseInt(Position);
        if (pos.HasValue) dict["position"] = pos.Value;
        return dict;
    }

    private object BuildPatch()
    {
        var dict = new Dictionary<string, object> { ["item_id"] = ItemId };
        if (ParseInt(CrossfadeMs) is int cf) dict["crossfade_ms"]  = cf;
        if (ParseInt(LeadInMs)    is int li) dict["lead_in_ms"]    = li;
        if (ParseInt(TrimStartMs) is int ts) dict["trim_start_ms"] = ts;
        if (ParseInt(TrimEndMs)   is int te) dict["trim_end_ms"]   = te;
        if (!string.IsNullOrWhiteSpace(SegueType)) dict["segue_type"] = SegueType;
        dict["auto_link_next"] = AutoLinkNext;
        return dict;
    }

    private object BuildExecute() => new
    {
        path           = ExecPath,
        arguments      = ExecArguments,
        working_dir    = ExecWorkingDir,
        timeout_ms     = ParseInt(ExecTimeoutMs) ?? 30000,
        capture_output = ExecCaptureOutput
    };

    private object BuildHttp() => new
    {
        method     = HttpMethod,
        url        = HttpUrl,
        body       = HttpBody,
        timeout_ms = ParseInt(HttpTimeoutMs) ?? 30000
    };

    public static ActionItemViewModel FromDto(ScheduledEventActionDto dto)
    {
        var vm = new ActionItemViewModel
        {
            SelectedGroup = ActionCatalog.GroupForType(dto.Type),
            SelectedType  = ActionCatalog.OptionForType(dto.Type),
        };

        if (dto.Payload is not JsonElement je || je.ValueKind != JsonValueKind.Object)
            return vm;

        switch (dto.Type)
        {
            case "CHANGE_PLAYLIST_MODE":    vm.PlaylistMode = Str(je, "mode", "AUTO"); break;
            case "CHANGE_SWEEPER_CATEGORY": vm.CategoryName = Str(je, "format_name"); break;
            case "CHANGE_SWEEPER_SUBCATEGORY": vm.SweeperSubcategoryName = Str(je, "subcategory_name"); break;
            case "PLAY_FILE":               vm.AssetId = Str(je, "asset_id"); break;
            case "ADD_ITEM":
                vm.AssetId  = Str(je, "asset_id");
                vm.Position = IntStr(je, "position");
                break;
            case "ADD_EXTERNAL_ITEM":
                vm.FilePath = Str(je, "file_path");
                vm.Title    = Str(je, "title");
                vm.Artist   = Str(je, "artist");
                vm.Position = IntStr(je, "position");
                break;
            case "LOAD_PLAYLIST": vm.PlaylistId = Str(je, "playlist_id"); break;
            case "REMOVE_ITEM":   vm.ItemId = Str(je, "item_id"); break;
            case "REORDER_ITEM":
                vm.ItemId      = Str(je, "item_id");
                vm.NewPosition = IntStr(je, "new_position");
                break;
            case "PATCH_ITEM":
                vm.ItemId       = Str(je, "item_id");
                vm.CrossfadeMs  = IntStr(je, "crossfade_ms");
                vm.LeadInMs     = IntStr(je, "lead_in_ms");
                vm.TrimStartMs  = IntStr(je, "trim_start_ms");
                vm.TrimEndMs    = IntStr(je, "trim_end_ms");
                vm.SegueType    = Str(je, "segue_type", "AUTO");
                vm.AutoLinkNext = Bool(je, "auto_link_next");
                break;
            case "WAIT": vm.WaitMs = IntStr(je, "duration_ms"); break;
            case "EXECUTE_FILE":
                vm.ExecPath          = Str(je, "path");
                vm.ExecArguments     = Str(je, "arguments");
                vm.ExecWorkingDir    = Str(je, "working_dir");
                vm.ExecTimeoutMs     = IntStr(je, "timeout_ms", "30000");
                vm.ExecCaptureOutput = Bool(je, "capture_output");
                break;
            case "HTTP_CALL":
                vm.HttpMethod    = Str(je, "method", "GET");
                vm.HttpUrl       = Str(je, "url");
                vm.HttpBody      = Str(je, "body");
                vm.HttpTimeoutMs = IntStr(je, "timeout_ms", "30000");
                break;
        }
        return vm;
    }

    // ── Small parsing helpers ────────────────────────────────────────────────

    private static int? ParseInt(string s) => int.TryParse(s, out var n) ? n : null;

    private static string Str(JsonElement je, string name, string fallback = "")
        => je.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? fallback
            : fallback;

    private static string IntStr(JsonElement je, string name, string fallback = "")
    {
        if (!je.TryGetProperty(name, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var n) => n.ToString(),
            JsonValueKind.String => el.GetString() ?? fallback,
            _ => fallback
        };
    }

    private static bool Bool(JsonElement je, string name)
        => je.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;
}

// ── Hour checkbox VM (0–23, used by the Repeat editor) ────────────────────────

public sealed partial class HourItemViewModel : ObservableObject
{
    public int    Hour  { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isSelected;

    public HourItemViewModel(int hour)
    {
        Hour  = hour;
        Label = hour.ToString("D2");
    }
}

// ── Main ViewModel ────────────────────────────────────────────────────────────

public sealed partial class ScheduledEventsViewModel : ObservableObject, IDisposable
{
    private readonly ApiClientService                  _api;
    private readonly ILogger<ScheduledEventsViewModel> _logger;

    private string? _editingEventId;

    // ── List state ────────────────────────────────────────────────────────────

    public ObservableCollection<ScheduledEventRowViewModel> Items { get; } = new();
    public ObservableCollection<ScheduledEventRowViewModel> FilteredItems { get; } = new();

    [ObservableProperty] private ScheduledEventRowViewModel? _selectedEvent;
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool   _hasNoActions = true;

    // ── List filters (which day's events are shown) ──────────────────────────

    public const string AllCategoriesLabel = "Wszystkie kategorie";

    private const string AllStatusLabel      = "Wszystkie";
    private const string EnabledStatusLabel  = "Aktywne";
    private const string DisabledStatusLabel = "Nieaktywne";

    public ObservableCollection<string> CategoryFilters { get; } = new() { AllCategoriesLabel };
    public IReadOnlyList<string> StatusFilters { get; } = [AllStatusLabel, EnabledStatusLabel, DisabledStatusLabel];

    [ObservableProperty] private DateTime _filterDate = DateTime.Today;
    [ObservableProperty] private string   _selectedCategoryFilter = AllCategoriesLabel;
    [ObservableProperty] private string   _selectedStatusFilter = AllStatusLabel;
    [ObservableProperty] private bool     _showAllDates;

    partial void OnFilterDateChanged(DateTime value) => ApplyFilters();
    partial void OnSelectedCategoryFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedStatusFilterChanged(string value) => ApplyFilters();
    partial void OnShowAllDatesChanged(bool value) => ApplyFilters();

    // ── Editor visibility ─────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorHeader))]
    private bool _isEditorVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorHeader))]
    private bool _isCreating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorHeader))]
    [NotifyPropertyChangedFor(nameof(IsOneTimeType))]
    private bool _isRepeatType = true;

    public bool IsOneTimeType => !IsRepeatType;

    public string EditorHeader => IsCreating
        ? Localizer.Instance?["se.hdr.new_event"] ?? "New event"
        : string.Format(Localizer.Instance?["se.hdr.edit"] ?? "Edit: {0}", EditorName);

    // ── Editor fields ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorHeader))]
    private string _editorName = "";

    [ObservableProperty] private string _editorEventType  = "Repeat";
    [ObservableProperty] private string _editorCategory   = "";
    [ObservableProperty] private bool   _editorEnabled    = true;
    [ObservableProperty] private bool   _editorSmartTiming;
    [ObservableProperty] private string _editorEventHour  = "00:00:00";

    public ObservableCollection<HourItemViewModel> EditorHours { get; } =
        new(Enumerable.Range(0, 24).Select(h => new HourItemViewModel(h)));

    // Date for ONE_TIME events (bound to CalendarDatePicker).
    [ObservableProperty] private DateTime? _editorOnlyOnDate = DateTime.Today;

    // Days of week
    [ObservableProperty] private bool _dayMon;
    [ObservableProperty] private bool _dayTue;
    [ObservableProperty] private bool _dayWed;
    [ObservableProperty] private bool _dayThu;
    [ObservableProperty] private bool _dayFri;
    [ObservableProperty] private bool _daySat;
    [ObservableProperty] private bool _daySun;

    public ObservableCollection<ActionItemViewModel> EditorActions { get; } = new();

    public IReadOnlyList<string> EventTypes { get; } = ["Repeat", "OneTime"];

    // ── Constructor ───────────────────────────────────────────────────────────

    public ScheduledEventsViewModel(
        ApiClientService                  api,
        ILogger<ScheduledEventsViewModel> logger)
    {
        _api    = api;
        _logger = logger;
    }

    public void Activate()
    {
        _api.ScheduleChanged += OnScheduleChanged;
        _ = LoadEventsAsync();
        _ = LoadPlaylistOptionsAsync();
    }

    private async Task LoadPlaylistOptionsAsync()
    {
        try
        {
            var envelope = await _api.GetSavedPlaylistsAsync();

            ActionItemViewModel.AvailablePlaylists.Clear();
            if (envelope is not null)
                foreach (var p in envelope.Items.OrderBy(p => p.Name))
                    ActionItemViewModel.AvailablePlaylists.Add(new PlaylistOption(p.PlaylistId, p.Name));

            // Editor may already be open (or a LOAD_PLAYLIST row already loaded from a saved
            // event) before this finishes — re-resolve each row's picker against the now-full list.
            foreach (var action in EditorActions)
                action.RefreshPlaylistSelection();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load playlists for action editor failed"); }
    }

    public void Dispose()
    {
        _api.ScheduleChanged -= OnScheduleChanged;
    }

    private void OnScheduleChanged()
    {
        Dispatcher.UIThread.Post(() => _ = LoadEventsAsync());
    }

    // ── Property change ───────────────────────────────────────────────────────

    partial void OnEditorEventTypeChanged(string value)
        => IsRepeatType = value == "Repeat";

    // ── List commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadEventsAsync()
    {
        IsBusy = true;
        try
        {
            var envelope = await _api.GetEventsAsync();
            if (envelope is null) return;

            Items.Clear();
            foreach (var dto in envelope.Items.OrderBy(e => e.Name))
                Items.Add(new ScheduledEventRowViewModel(dto));

            RefreshCategoryFilters();
            ApplyFilters();

            StatusMessage = string.Format(Localizer.Instance?["se.msg.count"] ?? "Events: {0}", envelope.Items.Count);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load events failed"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync()
    {
        if (SelectedEvent is null) return;
        IsBusy = true;
        try
        {
            var ok = await _api.PatchEventAsync(
                SelectedEvent.EventId,
                new ScheduledEventPatchDto(Enabled: !SelectedEvent.Enabled, SkipNext: null));
            if (ok) await LoadEventsAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Toggle enabled failed"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ToggleSkipNextAsync()
    {
        if (SelectedEvent is null) return;
        IsBusy = true;
        try
        {
            var ok = await _api.PatchEventAsync(
                SelectedEvent.EventId,
                new ScheduledEventPatchDto(Enabled: null, SkipNext: !SelectedEvent.SkipNext));
            if (ok) await LoadEventsAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Toggle skip failed"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteEventAsync()
    {
        if (SelectedEvent is null) return;
        IsBusy = true;
        try
        {
            var ok = await _api.DeleteEventAsync(SelectedEvent.EventId);
            if (ok)
            {
                if (_editingEventId == SelectedEvent.EventId)
                    IsEditorVisible = false;
                Items.Remove(SelectedEvent);
                SelectedEvent = null;
                RefreshCategoryFilters();
                ApplyFilters();
                StatusMessage = Localizer.Instance?["se.msg.deleted"] ?? "Event deleted";
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Delete event failed"); }
        finally { IsBusy = false; }
    }

    // ── Editor open/close ─────────────────────────────────────────────────────

    [RelayCommand]
    private void NewEvent()
    {
        _editingEventId = null;
        IsCreating      = true;
        ClearEditorFields();
        IsEditorVisible = true;
    }

    [RelayCommand]
    private void EditEvent()
    {
        if (SelectedEvent is null) return;
        _editingEventId = SelectedEvent.EventId;
        IsCreating      = false;
        LoadEditorFields(SelectedEvent.Source);
        IsEditorVisible = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditorVisible = false;
        ClearEditorFields();
    }

    // ── Editor save ───────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveEventAsync()
    {
        if (string.IsNullOrWhiteSpace(EditorName))
        {
            StatusMessage = Localizer.Instance?["se.msg.name_required"] ?? "Name is required";
            return;
        }

        IsBusy = true;
        try
        {
            var dto = BuildCreateDto();

            if (_editingEventId is null)
            {
                var resp = await _api.CreateEventAsync(dto);
                StatusMessage = resp is not null
                    ? string.Format(Localizer.Instance?["se.msg.created"] ?? "Created: {0}", resp.Name)
                    : Localizer.Instance?["se.msg.create_error"] ?? "Error creating event";
            }
            else
            {
                var ok = await _api.UpdateEventAsync(_editingEventId, dto);
                StatusMessage = ok
                    ? Localizer.Instance?["se.msg.changes_saved"] ?? "Changes saved"
                    : Localizer.Instance?["se.msg.save_error"]    ?? "Save error";
            }

            IsEditorVisible = false;
            await LoadEventsAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Save event failed"); }
        finally { IsBusy = false; }
    }

    // ── Action commands ───────────────────────────────────────────────────────

    [RelayCommand]
    private void AddAction()
    {
        EditorActions.Add(new ActionItemViewModel());
        RenumberActions();
    }

    [RelayCommand]
    private void RemoveAction(ActionItemViewModel action)
    {
        EditorActions.Remove(action);
        RenumberActions();
    }

    [RelayCommand]
    private void DuplicateAction(ActionItemViewModel action)
    {
        var index = EditorActions.IndexOf(action);
        if (index < 0) return;
        var duplicate = ActionItemViewModel.FromDto(action.ToDto());
        duplicate.AssetDisplayName = action.AssetDisplayName;
        EditorActions.Insert(index + 1, duplicate);
        RenumberActions();
    }

    [RelayCommand]
    private void MoveActionUp(ActionItemViewModel action)
    {
        var index = EditorActions.IndexOf(action);
        if (index <= 0) return;
        EditorActions.Move(index, index - 1);
        RenumberActions();
    }

    [RelayCommand]
    private void MoveActionDown(ActionItemViewModel action)
    {
        var index = EditorActions.IndexOf(action);
        if (index < 0 || index >= EditorActions.Count - 1) return;
        EditorActions.Move(index, index + 1);
        RenumberActions();
    }

    private void RenumberActions()
    {
        for (int i = 0; i < EditorActions.Count; i++)
            EditorActions[i].Index = i + 1;
        HasNoActions = EditorActions.Count == 0;
    }

    // ── Filtering (which day's events are shown in the list) ─────────────────

    private void RefreshCategoryFilters()
    {
        var previous   = SelectedCategoryFilter;
        var categories = Items
            .Select(i => i.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        CategoryFilters.Clear();
        CategoryFilters.Add(AllCategoriesLabel);
        foreach (var c in categories) CategoryFilters.Add(c);

        SelectedCategoryFilter = CategoryFilters.Contains(previous) ? previous : AllCategoriesLabel;
    }

    private void ApplyFilters()
    {
        FilteredItems.Clear();

        var dayAbbr = DayAbbr(FilterDate.DayOfWeek);
        var dateOnly = DateOnly.FromDateTime(FilterDate);

        foreach (var item in Items)
        {
            if (SelectedCategoryFilter != AllCategoriesLabel &&
                !string.Equals(item.Category, SelectedCategoryFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (SelectedStatusFilter == EnabledStatusLabel && !item.Enabled)
                continue;
            if (SelectedStatusFilter == DisabledStatusLabel && item.Enabled)
                continue;

            bool matchesDate = ShowAllDates ||
                (item.EventType == "OneTime"
                    ? DateOnly.TryParse(item.Source.OnlyOnDate, out var d) && d == dateOnly
                    : item.Source.Days.Contains(dayAbbr, StringComparer.OrdinalIgnoreCase));

            if (matchesDate)
                FilteredItems.Add(item);
        }
    }

    private static string DayAbbr(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday    => "MON",
        DayOfWeek.Tuesday   => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday  => "THU",
        DayOfWeek.Friday    => "FRI",
        DayOfWeek.Saturday  => "SAT",
        DayOfWeek.Sunday    => "SUN",
        _                   => string.Empty
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ClearEditorFields()
    {
        EditorName        = "";
        EditorEventType   = "Repeat";
        EditorCategory    = "";
        EditorEnabled     = true;
        EditorSmartTiming = false;
        EditorEventHour   = "00:00:00";
        EditorOnlyOnDate  = DateTime.Today;
        DayMon = DayTue = DayWed = DayThu = DayFri = DaySat = DaySun = false;
        foreach (var h in EditorHours) h.IsSelected = false;
        EditorActions.Clear();
        RenumberActions();
    }

    private void LoadEditorFields(ScheduledEventDto dto)
    {
        EditorName        = dto.Name;
        EditorEventType   = dto.EventType;
        EditorCategory    = dto.Category;
        EditorEnabled     = dto.Enabled;
        EditorSmartTiming = dto.SmartTiming;
        EditorEventHour   = dto.EventHour ?? "00:00:00";
        foreach (var h in EditorHours) h.IsSelected = dto.Hours.Contains(h.Hour);
        EditorOnlyOnDate  = DateOnly.TryParse(dto.OnlyOnDate, out var d)
            ? d.ToDateTime(TimeOnly.MinValue)
            : DateTime.Today;

        DayMon = dto.Days.Contains("MON");
        DayTue = dto.Days.Contains("TUE");
        DayWed = dto.Days.Contains("WED");
        DayThu = dto.Days.Contains("THU");
        DayFri = dto.Days.Contains("FRI");
        DaySat = dto.Days.Contains("SAT");
        DaySun = dto.Days.Contains("SUN");

        EditorActions.Clear();
        foreach (var action in dto.Actions)
            EditorActions.Add(ActionItemViewModel.FromDto(action));
        RenumberActions();

        _ = ResolveAssetDisplayNamesAsync(EditorActions.ToList());
    }

    /// <summary>
    /// ADD_ITEM/PLAY_FILE payloads only persist asset_id — the title/artist shown right after
    /// picking an asset is not saved. Re-fetch it here so re-opening an event for edit shows
    /// the same "Tytuł — Artysta" label instead of the raw asset id.
    /// </summary>
    private async Task ResolveAssetDisplayNamesAsync(IReadOnlyList<ActionItemViewModel> actions)
    {
        foreach (var action in actions)
        {
            if (action.ActionType is not ("ADD_ITEM" or "PLAY_FILE")) continue;
            if (string.IsNullOrWhiteSpace(action.AssetId)) continue;

            try
            {
                var detail = await _api.GetAssetDetailAsync(action.AssetId);
                if (detail is null) continue;
                action.AssetDisplayName = string.IsNullOrWhiteSpace(detail.Artist)
                    ? detail.Title
                    : $"{detail.Title} — {detail.Artist}";
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Resolve asset display name failed for {AssetId}", action.AssetId);
            }
        }
    }

    private ScheduledEventCreateDto BuildCreateDto()
    {
        return new ScheduledEventCreateDto(
            Name:         EditorName,
            EventType:    EditorEventType,
            Category:     EditorCategory,
            Enabled:      EditorEnabled,
            EventHour:    string.IsNullOrWhiteSpace(EditorEventHour) ? null : EditorEventHour,
            Days:         GetSelectedDays(),
            Hours:        GetSelectedHours(),
            SmartTiming:  EditorSmartTiming,
            Actions:      EditorActions.Select(a => a.ToDto()).ToList(),
            OnlyOnDate:   EditorEventType == "OneTime"
                              ? EditorOnlyOnDate?.ToString("yyyy-MM-dd")
                              : null
        );
    }

    private IReadOnlyList<string> GetSelectedDays()
    {
        var days = new List<string>(7);
        if (DayMon) days.Add("MON");
        if (DayTue) days.Add("TUE");
        if (DayWed) days.Add("WED");
        if (DayThu) days.Add("THU");
        if (DayFri) days.Add("FRI");
        if (DaySat) days.Add("SAT");
        if (DaySun) days.Add("SUN");
        return days;
    }

    private IReadOnlyList<int> GetSelectedHours() =>
        EditorHours.Where(h => h.IsSelected).Select(h => h.Hour).ToList();
}
