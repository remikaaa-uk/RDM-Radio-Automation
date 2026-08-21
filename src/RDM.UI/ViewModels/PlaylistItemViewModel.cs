using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;

namespace RDM.UI.ViewModels;

public sealed partial class PlaylistItemViewModel : ObservableObject
{
    // ── Immutable fields (set once from DTO) ──────────────────────────────────

    public string  ItemId           { get; }
    public string? AssetId          { get; }
    [ObservableProperty] private uint _position;
    public string  ItemType         { get; }   // TRACK | DUMMY | SWEEPER | CART
    public string? FormatName       { get; }
    public string? Artist           { get; }
    public string  Title            { get; }
    public string  DurationFormatted{ get; }
    public string  IntroFormatted   { get; }
    public string  OutroFormatted   { get; }
    public uint?   IntroCueMs       { get; }
    public uint?   OutroCueMs       { get; }
    public uint?   StartNextCueMs   { get; }
    public uint    DurationMs       { get; }
    public string? Comments         { get; }
    public bool    AutoLinkNext     { get; }

    // ── Mutable state ─────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isDamaged;
    [ObservableProperty] private bool _isCursorPosition;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowBackground))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowBackground))]
    private bool _isNext;

    // Estimated on-air time — updated every 1 s by PlaylistViewModel.ETA engine.
    [ObservableProperty] private string _scheduledAtText = "";

    // True when player is STOPPED/IDLE — ETA is a "press play now" estimate, shown in grey.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EtaBrush))]
    private bool _isEtaEstimated;

    [ObservableProperty] private bool _isPflAvailable;

    // ── Derived ───────────────────────────────────────────────────────────────

    public bool HasIntro => ItemType == "TRACK";
    public bool HasOutro => ItemType == "TRACK";

    public IBrush EtaBrush =>
        IsEtaEstimated
            ? GetBrush("PlaylistEtaStoppedBrush") ?? new SolidColorBrush(Color.Parse("#606070"))
            : GetBrush("PlaylistEtaBrush")        ?? new SolidColorBrush(Color.Parse("#4AB5FF"));

    public IBrush RowBackground
    {
        get
        {
            if (IsPlaying) return GetBrush("PlaylistPlayingBorderBrush") ?? new SolidColorBrush(Color.Parse("#1E4D2B"));
            if (IsNext)    return GetBrush("PlaylistNextBackground")     ?? new SolidColorBrush(Color.Parse("#1A1A2E"));
            return GetBrush("PlaylistTrackBackground") ?? new SolidColorBrush(Color.Parse("#1C1C2E"));
        }
    }

    public IBrush TypeAccentColor
    {
        get
        {
            if (FormatName is not null)
            {
                var b = GetBrush("PlaylistFormat_" + FormatName);
                if (b is not null) return b;
            }

            return ItemType.ToUpperInvariant() switch
            {
                "SWEEPER" => GetBrush("PlaylistSweeperBackground") ?? new SolidColorBrush(Color.Parse("#00233A")),
                "CART"    => GetBrush("PlaylistCartBackground")    ?? new SolidColorBrush(Color.Parse("#331A00")),
                "DUMMY"   => GetBrush("PlaylistDummyBackground")   ?? new SolidColorBrush(Color.Parse("#332A00")),
                _         => GetBrush("PlaylistTrackBackground")   ?? new SolidColorBrush(Color.Parse("#1C1C2E")),
            };
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    // Wired by PlaylistViewModel after construction.
    public IRelayCommand RemoveCommand    { get; }
    public IRelayCommand PflCommand       { get; }
    public IRelayCommand InfoCommand      { get; }
    public IRelayCommand EditSegueCommand { get; }
    public IRelayCommand InsertAboveCommand   { get; }
    public IRelayCommand InsertBelowCommand   { get; }
    public IRelayCommand SetCursorHereCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public PlaylistItemViewModel(
        PlaylistItemDto          dto,
        Action<string>           onRemove,
        Action<string>           onPfl,
        Action<string?>          onInfo,
        Action<string>           onEditSegue,
        Action<string>           onInsertAbove,
        Action<string>           onInsertBelow,
        Action<string>           onSetCursor,
        ILibrarySelectionService selection,
        bool                     hasPflDevice = false)
    {
        ItemId     = dto.ItemId;
        AssetId    = dto.AssetId;
        Position   = dto.Position;
        ItemType   = dto.AssetType ?? dto.ItemType;
        FormatName = dto.FormatName;

        Artist    = dto.Artist;
        Title     = dto.Title
                    ?? dto.DummyLabel
                    ?? (dto.ExternalFilePath is not null
                        ? System.IO.Path.GetFileNameWithoutExtension(dto.ExternalFilePath)
                        : null)
                    ?? Localizer.Instance?["common.no_title"]
                    ?? "(no title)";
        DurationMs = dto.DurationMs ?? dto.DummyDurationMs ?? 0;
        DurationFormatted = FormatMs(DurationMs);
        Comments = string.IsNullOrWhiteSpace(dto.Comments) ? null : dto.Comments;
        AutoLinkNext = dto.AutoLinkNext;

        IntroCueMs     = dto.CueMarkers?.Intro      is double intro      ? (uint)(intro      * 1000) : null;
        OutroCueMs     = dto.CueMarkers?.Outro      is double outro      ? (uint)(outro      * 1000) : null;
        StartNextCueMs = dto.CueMarkers?.StartNext  is double startNext  ? (uint)(startNext  * 1000) : null;

        IntroFormatted = FormatMs(IntroCueMs) is { Length: > 0 } fmt ? fmt : "0:00";
        OutroFormatted = OutroCueMs.HasValue && DurationMs > OutroCueMs.Value
            ? FormatMs(DurationMs - OutroCueMs.Value)
            : "0:00";

        IsDamaged        = dto.IsDamaged == true;

        RemoveCommand      = new RelayCommand(() => onRemove(ItemId));
        PflCommand         = new RelayCommand(() => onPfl(ItemId));
        InfoCommand        = new RelayCommand(() => onInfo(AssetId), () => AssetId != null);
        EditSegueCommand   = new RelayCommand(() => onEditSegue(ItemId), () => AssetId != null);
        InsertAboveCommand   = new RelayCommand(() => onInsertAbove(ItemId), () => selection.HasSelection);
        InsertBelowCommand   = new RelayCommand(() => onInsertBelow(ItemId), () => selection.HasSelection);
        SetCursorHereCommand = new RelayCommand(() => onSetCursor(ItemId));
        IsPflAvailable       = hasPflDevice && AssetId != null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public void UpdateFrom(PlaylistItemDto dto, uint position)
    {
        Position  = position;
        IsDamaged = dto.IsDamaged == true;
    }

    private static string FormatMs(uint? ms)
    {
        if (ms is null || ms == 0) return "";
        var t = TimeSpan.FromMilliseconds(ms.Value);
        return t.TotalMinutes >= 60
            ? t.ToString(@"h\:mm\:ss")
            : t.ToString(@"m\:ss");
    }

    private static IBrush? GetBrush(string key)
    {
        if (Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var res) == true)
            return res as IBrush;
        return null;
    }
}
