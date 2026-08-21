using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace RDM.UI.ViewModels;

// ── Row VMs ───────────────────────────────────────────────────────────────────

public sealed class SavedPlaylistRowViewModel
{
    public string PlaylistId    { get; }
    public string Name          { get; }
    public string ItemCountText { get; }
    public string DisplayDate   { get; }

    public SavedPlaylistRowViewModel(SavedPlaylistSummaryDto dto)
    {
        PlaylistId    = dto.PlaylistId;
        Name          = dto.Name;
        ItemCountText = $"{dto.ItemCount} tr.";
        DisplayDate   = dto.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy");
    }
}

public sealed partial class BuilderItemViewModel : ObservableObject
{
    public string   ItemId            { get; }
    public string   AssetId           { get; }
    public string   Title             { get; }
    public string?  Artist            { get; }
    public string?  Album             { get; }
    public string   AssetType         { get; }
    public string?  FormatName        { get; }
    public string?  SubcategoryName   { get; }
    public string   DurationFormatted { get; }
    public string   IntroFormatted    { get; }
    public uint     DurationMs        { get; }
    public decimal? Bpm               { get; }
    public int?     Year              { get; }
    public byte?    Rating            { get; }
    public string?  Genre             { get; }
    public string?  Mood              { get; }
    public string?  Gender            { get; }
    public string?  Language          { get; }
    public decimal? LoudnessLufs      { get; }
    public string   TypeColor         { get; }

    internal PlaylistItemDto? OriginalDto { get; }

    private readonly CueMarkersDto? _cueMarkers;

    public CueMarkersDto? CueMarkers => OriginalDto?.CueMarkers ?? _cueMarkers;

    [ObservableProperty] private int    _displayPosition;
    [ObservableProperty] private string _timeAtPosition = "00:00:00";

    [ObservableProperty] private uint _crossfadeMs;
    [ObservableProperty] private uint _trimStartMs;
    [ObservableProperty] private uint _trimEndMs;

    public BuilderItemViewModel(AssetDto dto, int position)
    {
        ItemId            = Guid.NewGuid().ToString();
        AssetId           = dto.AssetId;
        Title             = dto.Title;
        Artist            = dto.Artist;
        Album             = dto.Album;
        AssetType         = dto.AssetType;
        FormatName        = dto.FormatName;
        SubcategoryName   = dto.SubcategoryName;
        DurationMs        = dto.DurationMs;
        Bpm               = dto.Bpm;
        Year              = dto.Year;
        Rating            = dto.Rating;
        Genre             = dto.Genre;
        Mood              = dto.Mood;
        Gender            = dto.Gender;
        Language          = dto.Language;
        LoudnessLufs      = dto.LoudnessLufs;
        DurationFormatted = FormatMs(dto.DurationMs);
        IntroFormatted    = FormatMs(dto.CueMarkers?.Intro is double i ? (uint)(i * 1000) : 0);
        DisplayPosition   = position;
        TypeColor         = TypeToColor(dto.AssetType);
        _cueMarkers       = dto.CueMarkers;
    }

    public BuilderItemViewModel(PlaylistItemDto dto, int position)
    {
        OriginalDto       = dto;
        ItemId            = dto.ItemId;
        AssetId           = dto.AssetId ?? "";
        Title             = dto.Title ?? dto.DummyLabel ?? Localizer.Instance?["common.no_title"] ?? "(no title)";
        Artist            = dto.Artist;
        AssetType         = dto.AssetType ?? dto.ItemType;
        FormatName        = dto.FormatName;
        DurationMs        = dto.DurationMs ?? dto.DummyDurationMs ?? 0;
        DurationFormatted = FormatMs(DurationMs);
        IntroFormatted    = FormatMs(dto.CueMarkers?.Intro is double i ? (uint)(i * 1000) : 0);
        DisplayPosition   = position;
        TypeColor         = TypeToColor(AssetType);
        _crossfadeMs      = dto.CrossfadeMs ?? 0;
        _trimStartMs      = dto.TrimStartMs ?? 0;
        _trimEndMs        = dto.TrimEndMs   ?? 0;
    }

    public BuilderItemViewModel(PlaylistFileItem fi, int position)
    {
        ItemId            = Guid.NewGuid().ToString();
        AssetId           = fi.AssetId;
        Title             = fi.Title;
        Artist            = fi.Artist;
        Album             = fi.Album;
        AssetType         = fi.AssetType;
        FormatName        = fi.FormatName;
        SubcategoryName   = fi.SubcategoryName;
        DurationMs        = fi.DurationMs;
        Bpm               = fi.Bpm;
        Year              = fi.Year;
        Rating            = fi.Rating;
        Genre             = fi.Genre;
        Mood              = fi.Mood;
        Gender            = fi.Gender;
        Language          = fi.Language;
        LoudnessLufs      = fi.LoudnessLufs;
        DurationFormatted = FormatMs(fi.DurationMs);
        IntroFormatted    = FormatMs(fi.CueMarkers?.Intro is double i ? (uint)(i * 1000) : 0);
        DisplayPosition   = position;
        TypeColor         = TypeToColor(fi.AssetType);
        _crossfadeMs      = fi.CrossfadeMs ?? 0;
        _trimStartMs      = fi.TrimStartMs ?? 0;
        _trimEndMs        = fi.TrimEndMs   ?? 0;
        _cueMarkers       = fi.CueMarkers;
    }

    internal PlaylistItemDto ToSegueDto(uint position) =>
        OriginalDto ?? new PlaylistItemDto(
            ItemId:           ItemId,
            AssetId:          string.IsNullOrEmpty(AssetId) ? null : AssetId,
            Position:         position,
            ItemType:         "ASSET",
            ExternalFilePath: null,
            DummyLabel:       null,
            DummyNote:        null,
            DummyDurationMs:  null,
            CrossfadeMs:      CrossfadeMs > 0 ? CrossfadeMs : null,
            LeadInMs:         null,
            TrimStartMs:      TrimStartMs > 0 ? TrimStartMs : null,
            TrimEndMs:        TrimEndMs   > 0 ? TrimEndMs   : null,
            SegueType:        "AUTO",
            ScheduledAt:      null,
            AutoLinkNext:     false,
            Title:            Title,
            Artist:           Artist,
            DurationMs:       DurationMs,
            AssetType:        AssetType,
            FormatName:       FormatName,
            Status:           null,
            IsDamaged:        null,
            Comments:         null,
            CueMarkers:       _cueMarkers);

    private static string TypeToColor(string? assetType) => assetType switch
    {
        "SWEEPER"     => "#4AB5FF",
        "CART"        => "#FFD700",
        "VOICE_TRACK" => "#C8A0FF",
        _             => "#B0B0C5"
    };

    internal static string FormatMs(uint ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.ToString(@"hh\:mm\:ss");
    }
}

public sealed class BuilderLibraryItemViewModel
{
    private readonly AssetDto _dto;

    public string   AssetId           { get; }
    public string   Title             { get; }
    public string?  Artist            { get; }
    public string   AssetType         { get; }
    public string   DurationFormatted { get; }
    public string   IntroFormatted    { get; }
    public string   BpmFormatted      { get; }
    public uint     DurationMs        { get; }
    public uint     IntroMs           { get; }
    public decimal? Bpm               { get; }
    public string   TypeColor         { get; }

    public BuilderLibraryItemViewModel(AssetDto dto)
    {
        _dto              = dto;
        AssetId           = dto.AssetId;
        Title             = dto.Title;
        Artist            = dto.Artist;
        AssetType         = dto.AssetType;
        DurationMs        = dto.DurationMs;
        IntroMs           = dto.CueMarkers?.Intro is double i ? (uint)(i * 1000) : 0;
        DurationFormatted = BuilderItemViewModel.FormatMs(dto.DurationMs);
        IntroFormatted    = BuilderItemViewModel.FormatMs(IntroMs);
        Bpm               = dto.Bpm;
        BpmFormatted      = dto.Bpm is > 0 ? ((int)dto.Bpm.Value).ToString() : "";
        TypeColor         = TypeToColor(dto.AssetType);
    }

    private static string TypeToColor(string? assetType) => assetType switch
    {
        "SWEEPER"     => "#4AB5FF",
        "CART"        => "#FFD700",
        "VOICE_TRACK" => "#C8A0FF",
        _             => "#B0B0C5"
    };

    public BuilderItemViewModel ToBuilderItem(int position) => new(_dto, position);
}

// ── Main ViewModel ────────────────────────────────────────────────────────────

public sealed partial class PlaylistBuilderViewModel : ObservableObject, IDisposable
{
    private readonly ApiClientService                  _api;
    private readonly INavigationService               _navigation;
    private readonly IConfiguration                   _config;
    private readonly ILogger<PlaylistBuilderViewModel> _logger;

    private CancellationTokenSource? _searchCts;
    private readonly DispatcherTimer  _debounceTimer;

    private int     _totalCount;
    private int     _pageOffset;
    private string? _loadedPlaylistId;

    public string? LoadedPlaylistId => _loadedPlaylistId;

    private int PageSize => _config.GetValue("general:library_page_size", 50);

    // ── Observable collections ────────────────────────────────────────────────

    public ObservableCollection<SavedPlaylistRowViewModel>   SavedPlaylists     { get; } = new();
    public ObservableCollection<BuilderItemViewModel>        BuilderItems       { get; } = new();
    public ObservableCollection<BuilderLibraryItemViewModel> LibraryItems       { get; } = new();
    public ObservableCollection<AssetFormatDto>              Formats            { get; } = new();
    public ObservableCollection<SubcategoryDto>              SubcategoryFilters { get; } = new();
    public ObservableCollection<string>                      GenreFilters       { get; } = new(["Any Genre"]);

    // ── Playlist state ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSegueCommand))]
    private BuilderItemViewModel? _selectedBuilderItem;

    /// Multi-selection from the playlist list, synced from code-behind.
    private IReadOnlyList<BuilderItemViewModel> _selectedBuilderItems =
        Array.Empty<BuilderItemViewModel>();

    [ObservableProperty] private SavedPlaylistRowViewModel? _selectedSavedPlaylist;
    [ObservableProperty] private string _playlistName      = "New Playlist";
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private bool   _hasUnsavedChanges;

    // ── Library state ─────────────────────────────────────────────────────────

    [ObservableProperty] private string          _librarySearch       = "";
    [ObservableProperty] private AssetFormatDto? _selectedFormat;
    [ObservableProperty] private SubcategoryDto? _selectedSubcategory;
    [ObservableProperty] private string          _selectedGenre       = "Any Genre";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayPflCommand))]
    private BuilderLibraryItemViewModel? _selectedLibraryItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayPflCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopPflCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeekBackwardCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeekForwardCommand))]
    private bool _isPflPlaying;

    // ── Computed ──────────────────────────────────────────────────────────────

    public string TotalDurationText
    {
        get
        {
            var totalMs = BuilderItems.Sum(i => (long)i.DurationMs);
            var t = TimeSpan.FromMilliseconds(totalMs);
            return $"TOTAL: {t:hh\\:mm\\:ss}";
        }
    }

    public bool HasPreviousPage => _pageOffset > 0;
    public bool HasNextPage     => _pageOffset + LibraryItems.Count < _totalCount;

    public string PaginationText
    {
        get
        {
            if (_totalCount == 0) return "0/0";
            var from = _pageOffset + 1;
            var to   = _pageOffset + LibraryItems.Count;
            return $"{from}-{to}/{_totalCount}";
        }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public PlaylistBuilderViewModel(
        ApiClientService                  api,
        INavigationService               navigation,
        IConfiguration                   config,
        ILogger<PlaylistBuilderViewModel> logger)
    {
        _api        = api;
        _navigation = navigation;
        _config     = config;
        _logger     = logger;

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _pageOffset = 0;
            _ = SearchLibraryAsync();
        };
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Activate()
    {
        _api.PflEnded += OnPflEnded;
        _ = Task.WhenAll(LoadSavedPlaylistsAsync(), LoadFormatsAsync(), LoadGenreFiltersAsync(), SearchLibraryAsync());
    }

    private void OnPflEnded()
    {
        Dispatcher.UIThread.Post(() => IsPflPlaying = false);
    }

    // ── Property change ───────────────────────────────────────────────────────

    partial void OnLibrarySearchChanged(string value)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    partial void OnSelectedFormatChanged(AssetFormatDto? value)
    {
        _ = LoadSubcategoriesAsync(value?.FormatId);
        _pageOffset = 0;
        _ = SearchLibraryAsync();
    }

    partial void OnSelectedSubcategoryChanged(SubcategoryDto? value)
    {
        _pageOffset = 0;
        _ = SearchLibraryAsync();
    }

    partial void OnSelectedGenreChanged(string value)
    {
        _pageOffset = 0;
        _ = SearchLibraryAsync();
    }

    // ── Saved playlist commands ───────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadSavedPlaylistsAsync()
    {
        try
        {
            var envelope = await _api.GetSavedPlaylistsAsync();
            if (envelope is null) return;

            SavedPlaylists.Clear();
            foreach (var dto in envelope.Items.OrderByDescending(p => p.CreatedAt))
                SavedPlaylists.Add(new SavedPlaylistRowViewModel(dto));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load saved playlists failed"); }
    }

    [RelayCommand]
    private async Task OpenSavedPlaylistAsync()
    {
        if (SelectedSavedPlaylist is null) return;
        IsBusy = true;
        try
        {
            var detail = await _api.GetSavedPlaylistDetailAsync(SelectedSavedPlaylist.PlaylistId);
            if (detail is null) return;

            PlaylistName = detail.Name;
            BuilderItems.Clear();
            for (int i = 0; i < detail.Items.Count; i++)
                BuilderItems.Add(new BuilderItemViewModel(detail.Items[i], i + 1));

            RecalculateTimes();
            HasUnsavedChanges = false;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Open playlist failed"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteSavedPlaylistAsync()
    {
        if (SelectedSavedPlaylist is null) return;
        IsBusy = true;
        try
        {
            var ok = await _api.DeleteSavedPlaylistAsync(SelectedSavedPlaylist.PlaylistId);
            if (ok)
            {
                SavedPlaylists.Remove(SelectedSavedPlaylist);
                SelectedSavedPlaylist = null;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Delete playlist failed"); }
        finally { IsBusy = false; }
    }

    // ── Playlist commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private void NewPlaylist()
    {
        BuilderItems.Clear();
        PlaylistName          = "New Playlist";
        SelectedSavedPlaylist = null;
        HasUnsavedChanges     = false;
        _loadedPlaylistId     = null;
        RecalculateTimes();
    }

    [RelayCommand]
    private void RemoveSelectedItem()
    {
        if (SelectedBuilderItem is null) return;
        BuilderItems.Remove(SelectedBuilderItem);
        HasUnsavedChanges = true;
        RecalculateTimes();
    }

    [RelayCommand]
    private void MoveItemUp()
    {
        if (SelectedBuilderItem is null) return;
        var idx = BuilderItems.IndexOf(SelectedBuilderItem);
        if (idx <= 0) return;
        BuilderItems.Move(idx, idx - 1);
        HasUnsavedChanges = true;
        RecalculateTimes();
    }

    [RelayCommand]
    private void MoveItemDown()
    {
        if (SelectedBuilderItem is null) return;
        var idx = BuilderItems.IndexOf(SelectedBuilderItem);
        if (idx < 0 || idx >= BuilderItems.Count - 1) return;
        BuilderItems.Move(idx, idx + 1);
        HasUnsavedChanges = true;
        RecalculateTimes();
    }

    /// Updates the multi-selection set; called from the view's SelectionChanged handler.
    public void UpdateSelection(IEnumerable<BuilderItemViewModel> items)
    {
        _selectedBuilderItems = items.ToList();
        EditSegueCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditSegue))]
    private async Task EditSegue()
    {
        var items = ResolveSegueItems();
        if (items is null) return;

        var window = await _navigation.ShowModalAsync<RDM.UI.Views.SegueEditorWindow>(items);

        // After the editor closes, sync updated segue parameters back to builder items
        // and insert any new voice-track clips at their position in the chain.
        if (window.DataContext is SegueEditorViewModel segueVm)
        {
            int insertAt = BuilderItems.Count;   // fallback: append if no predecessor matched
            foreach (var clip in segueVm.Clips)
            {
                if (clip.IsNew)
                {
                    if (clip.AssetId is null) continue;   // import failed → nothing to insert
                    var dto = new AssetDto(
                        AssetId:         clip.AssetId,
                        AssetType:       "VOICE_TRACK",
                        FormatName:      null,
                        SubcategoryName: null,
                        Title:           clip.Title,
                        Artist:          clip.Artist,
                        Album:           null,
                        DurationMs:     clip.DurationMs,
                        Bpm:            null,
                        Year:           null,
                        Rating:         null,
                        Status:         "ACTIVE",
                        LoudnessLufs:   null,
                        Mood:           null,
                        Gender:         null,
                        Language:       null,
                        Genre:          null,
                        PlayCount:      0,
                        CreatedAt:      DateTime.UtcNow,
                        CueMarkers:     null);
                    var pos = Math.Clamp(insertAt, 0, BuilderItems.Count);
                    BuilderItems.Insert(pos, new BuilderItemViewModel(dto, pos + 1));
                    insertAt = pos + 1;
                    HasUnsavedChanges = true;
                    continue;
                }

                var bi = BuilderItems.FirstOrDefault(b => b.ItemId == clip.ItemId);
                if (bi is null) continue;
                bi.CrossfadeMs = clip.CrossfadeMs;
                bi.TrimStartMs = clip.TrimStartMs;
                bi.TrimEndMs   = clip.TrimEndMs;
                insertAt = BuilderItems.IndexOf(bi) + 1;
            }
            RecalculateTimes();
        }
    }

    /// Resolves the ordered PlaylistItemDtos to open in the segue editor.
    /// Uses multi-selection when ≥2 items are selected, otherwise selected + next.
    private IReadOnlyList<PlaylistItemDto>? ResolveSegueItems()
    {
        var multi = _selectedBuilderItems
            .Where(i => !string.IsNullOrEmpty(i.ItemId))
            .OrderBy(BuilderItems.IndexOf)
            .Select(i => i.ToSegueDto((uint)BuilderItems.IndexOf(i)))
            .ToList();
        if (multi.Count >= 2) return multi;

        if (SelectedBuilderItem is not { } sel || string.IsNullOrEmpty(sel.ItemId)) return null;
        var idx = BuilderItems.IndexOf(sel);
        if (idx < 0 || idx >= BuilderItems.Count - 1) return null;
        var next = BuilderItems[idx + 1];
        if (string.IsNullOrEmpty(next.ItemId)) return null;
        return new[] { sel.ToSegueDto((uint)idx), next.ToSegueDto((uint)(idx + 1)) };
    }

    private bool CanEditSegue
    {
        get
        {
            if (_selectedBuilderItems.Count(i => !string.IsNullOrEmpty(i.ItemId)) >= 2)
                return true;
            return SelectedBuilderItem is { } sel &&
                   !string.IsNullOrEmpty(sel.ItemId) &&
                   BuilderItems.IndexOf(sel) < BuilderItems.Count - 1;
        }
    }

    [RelayCommand]
    private async Task SavePlaylistAsync()
    {
        if (string.IsNullOrWhiteSpace(PlaylistName)) return;
        IsBusy = true;
        try
        {
            var items = BuilderItems.Select(i => new PlaylistItemSaveDto(
                AssetId:         i.AssetId,
                ItemType:        "ASSET",
                DummyLabel:      null,
                DummyNote:       null,
                DummyDurationMs: null,
                CrossfadeMs:     i.CrossfadeMs > 0 ? i.CrossfadeMs : null,
                TrimStartMs:     i.TrimStartMs > 0 ? i.TrimStartMs : null,
                TrimEndMs:       i.TrimEndMs   > 0 ? i.TrimEndMs   : null,
                SegueType:       i.OriginalDto?.SegueType ?? "AUTO",
                AutoLinkNext:    false
            )).ToList();

            var resp = await _api.SavePlaylistAsync(
                new SavePlaylistRequestDto(PlaylistName, items));

            if (resp is not null)
            {
                HasUnsavedChanges = false;
                await LoadSavedPlaylistsAsync();
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Save playlist failed"); }
        finally { IsBusy = false; }
    }

    // ── Cue bar stubs ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void SetStartCue() { }

    [RelayCommand]
    private void SetIntroCue() { }

    [RelayCommand]
    private void SetEndCue() { }

    // ── Library commands ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SearchLibraryAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        try
        {
            var formatId      = string.IsNullOrEmpty(SelectedFormat?.FormatId)           ? null : SelectedFormat!.FormatId;
            var subcategoryId = string.IsNullOrEmpty(SelectedSubcategory?.SubcategoryId) ? null : SelectedSubcategory!.SubcategoryId;
            var genreParam    = SelectedGenre == "Any Genre" ? null : SelectedGenre;

            var envelope = await _api.SearchAssetsAsync(
                q:             string.IsNullOrWhiteSpace(LibrarySearch) ? null : LibrarySearch,
                formatId:      formatId,
                subcategoryId: subcategoryId,
                genre:         genreParam,
                status:        "ACTIVE",
                limit:         PageSize,
                offset:        _pageOffset,
                ct:            ct);

            if (ct.IsCancellationRequested) return;
            if (envelope is null) return;

            _totalCount = envelope.Total;

            LibraryItems.Clear();
            foreach (var dto in envelope.Items)
                LibraryItems.Add(new BuilderLibraryItemViewModel(dto));

            UpdatePaginationState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Library search failed"); }
    }

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    private async Task PreviousPageAsync()
    {
        _pageOffset = Math.Max(0, _pageOffset - PageSize);
        await SearchLibraryAsync();
    }

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private async Task NextPageAsync()
    {
        _pageOffset += PageSize;
        await SearchLibraryAsync();
    }

    private void UpdatePaginationState()
    {
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(PaginationText));
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddSelectedToBuilder()
    {
        if (SelectedLibraryItem is null) return;
        var item = SelectedLibraryItem.ToBuilderItem(BuilderItems.Count + 1);
        BuilderItems.Add(item);
        HasUnsavedChanges = true;
        RecalculateTimes();
    }

    // ── PFL commands ─────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanPlayPfl))]
    private async Task PlayPflAsync(CancellationToken ct)
    {
        if (SelectedLibraryItem is null) return;
        try
        {
            await _api.StartPflAsync(SelectedLibraryItem.AssetId, ct);
            IsPflPlaying = true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PFL start failed"); }
    }
    private bool CanPlayPfl() => SelectedLibraryItem is not null && !IsPflPlaying;

    [RelayCommand(CanExecute = nameof(CanStopPfl))]
    private async Task StopPflAsync(CancellationToken ct)
    {
        try { await _api.StopPflAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "PFL stop failed"); }
        finally { IsPflPlaying = false; }
    }
    private bool CanStopPfl() => IsPflPlaying;

    [RelayCommand(CanExecute = nameof(CanSeekPfl))]
    private async Task SeekBackwardAsync(CancellationToken ct)
    {
        try { await _api.SeekPflAsync(-10_000, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "PFL seek backward failed"); }
    }

    [RelayCommand(CanExecute = nameof(CanSeekPfl))]
    private async Task SeekForwardAsync(CancellationToken ct)
    {
        try { await _api.SeekPflAsync(10_000, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "PFL seek forward failed"); }
    }
    private bool CanSeekPfl() => IsPflPlaying;

    // ── Helpers ───────────────────────────────────────────────────────────────

    public void MoveBuilderItem(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= BuilderItems.Count) return;
        if (toIndex   < 0 || toIndex   >= BuilderItems.Count) return;
        BuilderItems.Move(fromIndex, toIndex);
        HasUnsavedChanges = true;
        RecalculateTimes();
    }

    private void RecalculateTimes()
    {
        double startMs = 0;
        for (int i = 0; i < BuilderItems.Count; i++)
        {
            BuilderItems[i].DisplayPosition = i + 1;
            BuilderItems[i].TimeAtPosition  = BuilderItemViewModel.FormatMs((uint)Math.Max(0, startMs));

            if (i >= BuilderItems.Count - 1) continue;

            var item = BuilderItems[i];
            var trim = (double)item.TrimStartMs;
            var end  = item.TrimEndMs > 0 ? (double)item.TrimEndMs : (double)item.DurationMs;
            var effDuration = Math.Max(0, end - trim);

            // If this track has a StartNext marker within its audible range, use it
            // as the start point for the next track (takes priority over CrossfadeMs).
            if (item.CueMarkers?.StartNext is double sn)
            {
                var snMs = sn * 1000;
                if (snMs > trim && snMs <= end)
                {
                    startMs += snMs - trim;
                    continue;
                }
            }

            // Fall back to effective duration minus the next track's crossfade.
            // Items without an explicit CrossfadeMs contribute their full effective duration.
            var nextCf = (double)BuilderItems[i + 1].CrossfadeMs;
            startMs += Math.Max(0, effDuration - nextCf);
        }
        OnPropertyChanged(nameof(TotalDurationText));
    }

    // ── Library sorting ──────────────────────────────────────────────────────

    public void SortLibrary(string column, bool ascending)
    {
        var sorted = (column, ascending) switch
        {
            ("Artist",     true)  => LibraryItems.OrderBy(x => x.Artist ?? ""),
            ("Artist",     false) => LibraryItems.OrderByDescending(x => x.Artist ?? ""),
            ("Title",      true)  => LibraryItems.OrderBy(x => x.Title),
            ("Title",      false) => LibraryItems.OrderByDescending(x => x.Title),
            ("IntroMs",    true)  => LibraryItems.OrderBy(x => x.IntroMs),
            ("IntroMs",    false) => LibraryItems.OrderByDescending(x => x.IntroMs),
            ("DurationMs", true)  => LibraryItems.OrderBy(x => x.DurationMs),
            ("DurationMs", false) => LibraryItems.OrderByDescending(x => x.DurationMs),
            ("Bpm",        true)  => LibraryItems.OrderBy(x => x.Bpm ?? 0),
            ("Bpm",        false) => LibraryItems.OrderByDescending(x => x.Bpm ?? 0),
            _                    => LibraryItems.OrderBy(x => x.Artist ?? ""),
        };

        var list = sorted.ToList();
        LibraryItems.Clear();
        foreach (var item in list)
            LibraryItems.Add(item);
    }

    // ── Public dialog-driven operations ──────────────────────────────────────────

    public async Task<bool> SaveToDatabaseAsync(string name, string? overwriteId = null)
    {
        IsBusy = true;
        try
        {
            var items = BuilderItems.Select(i => new PlaylistItemSaveDto(
                AssetId:         i.AssetId,
                ItemType:        "ASSET",
                DummyLabel:      null,
                DummyNote:       null,
                DummyDurationMs: null,
                CrossfadeMs:     i.CrossfadeMs > 0 ? i.CrossfadeMs : null,
                TrimStartMs:     i.TrimStartMs > 0 ? i.TrimStartMs : null,
                TrimEndMs:       i.TrimEndMs   > 0 ? i.TrimEndMs   : null,
                SegueType:       i.OriginalDto?.SegueType ?? "AUTO",
                AutoLinkNext:    false
            )).ToList();

            var dto  = new SavePlaylistRequestDto(name, items);
            SavePlaylistResponseDto? resp;

            if (overwriteId is not null)
                resp = await _api.UpdateSavedPlaylistAsync(overwriteId, dto);
            else
                resp = await _api.SavePlaylistAsync(dto);

            if (resp is null) return false;

            PlaylistName          = name;
            HasUnsavedChanges     = false;
            _loadedPlaylistId     = resp.PlaylistId;
            await LoadSavedPlaylistsAsync();
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "SaveToDatabase failed"); return false; }
        finally { IsBusy = false; }
    }

    public async Task<bool> SaveToFileAsync(string name, string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
            return await SaveToM3uAsync(name, filePath);

        try
        {
            await PlaylistFileService.SaveAsync(filePath, name, BuilderItems);
            PlaylistName      = name;
            HasUnsavedChanges = false;
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "SaveToFile failed"); return false; }
    }

    private async Task<bool> SaveToM3uAsync(string name, string filePath)
    {
        IsBusy = true;
        try
        {
            var entries = new List<(string Title, string? Artist, uint DurationMs, string? AudioPath)>();
            foreach (var item in BuilderItems)
            {
                string? audioPath = null;
                if (!string.IsNullOrEmpty(item.AssetId))
                {
                    var detail = await _api.GetAssetDetailAsync(item.AssetId);
                    audioPath  = detail?.RdmFilePath;
                }
                entries.Add((item.Title, item.Artist, item.DurationMs, audioPath));
            }
            await PlaylistFileService.SaveM3uAsync(filePath, entries);
            PlaylistName      = name;
            HasUnsavedChanges = false;
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "SaveToM3u failed"); return false; }
        finally { IsBusy = false; }
    }

    public async Task<bool> LoadFromDatabaseAsync(string playlistId)
    {
        IsBusy = true;
        try
        {
            var detail = await _api.GetSavedPlaylistDetailAsync(playlistId);
            if (detail is null) return false;

            PlaylistName = detail.Name;
            BuilderItems.Clear();
            for (int i = 0; i < detail.Items.Count; i++)
                BuilderItems.Add(new BuilderItemViewModel(detail.Items[i], i + 1));

            RecalculateTimes();
            HasUnsavedChanges = false;
            _loadedPlaylistId = playlistId;
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "LoadFromDatabase failed"); return false; }
        finally { IsBusy = false; }
    }

    public async Task<bool> LoadFromFileAsync(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
            return await LoadFromM3uAsync(filePath);

        try
        {
            var doc = await PlaylistFileService.LoadAsync(filePath);
            if (doc is null) return false;

            PlaylistName = doc.Name;
            BuilderItems.Clear();
            for (int i = 0; i < doc.Items.Count; i++)
                BuilderItems.Add(new BuilderItemViewModel(doc.Items[i], i + 1));

            RecalculateTimes();
            HasUnsavedChanges = false;
            _loadedPlaylistId = null;
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "LoadFromFile failed"); return false; }
    }

    private async Task<bool> LoadFromM3uAsync(string filePath)
    {
        IsBusy = true;
        try
        {
            var entries = await PlaylistFileService.LoadM3uAsync(filePath);
            if (entries.Count == 0) return false;

            var matched = new List<BuilderItemViewModel>();
            foreach (var entry in entries)
            {
                // Try exact path match first, then search by title.
                AssetDto? asset = null;
                if (!string.IsNullOrEmpty(entry.FilePath))
                    asset = await _api.FindAssetByFilePathAsync(entry.FilePath);

                if (asset is null && !string.IsNullOrWhiteSpace(entry.Title))
                {
                    var results = await _api.SearchAssetsAsync(q: entry.Title, limit: 1);
                    asset = results?.Items.FirstOrDefault();
                }

                if (asset is null) continue;
                matched.Add(new BuilderItemViewModel(asset, matched.Count + 1));
            }

            if (matched.Count == 0) return false;

            PlaylistName = Path.GetFileNameWithoutExtension(filePath);
            BuilderItems.Clear();
            foreach (var item in matched)
                BuilderItems.Add(item);

            RecalculateTimes();
            HasUnsavedChanges = false;
            _loadedPlaylistId = null;
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "LoadFromM3u failed"); return false; }
        finally { IsBusy = false; }
    }

    public async Task<bool> DeleteFromDatabaseAsync(string playlistId)
    {
        IsBusy = true;
        try
        {
            var ok = await _api.DeleteSavedPlaylistAsync(playlistId);
            if (ok)
            {
                var row = SavedPlaylists.FirstOrDefault(p => p.PlaylistId == playlistId);
                if (row is not null) SavedPlaylists.Remove(row);
            }
            return ok;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "DeleteFromDatabase failed"); return false; }
        finally { IsBusy = false; }
    }

    // ── Filter helpers ────────────────────────────────────────────────────────

    private async Task LoadFormatsAsync()
    {
        try
        {
            var envelope = await _api.GetFormatsAsync();
            if (envelope is null) return;
            Formats.Clear();
            Formats.Add(new AssetFormatDto("", "Any Category", null));
            foreach (var f in envelope.Items)
                Formats.Add(f);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load formats failed"); }
    }

    private async Task LoadSubcategoriesAsync(string? formatId)
    {
        SubcategoryFilters.Clear();
        SelectedSubcategory = null;
        if (string.IsNullOrEmpty(formatId)) return;
        try
        {
            var envelope = await _api.GetSubcategoriesAsync(formatId);
            if (envelope is null) return;
            foreach (var s in envelope.Items)
                SubcategoryFilters.Add(s);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load subcategories failed"); }
    }

    private async Task LoadGenreFiltersAsync()
    {
        try
        {
            var envelope = await _api.GetGenresAsync();
            if (envelope is null) return;
            GenreFilters.Clear();
            GenreFilters.Add("Any Genre");
            foreach (var g in envelope.Items)
                GenreFilters.Add(g.Name);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load genre filters failed"); }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _api.PflEnded -= OnPflEnded;
        _searchCts?.Cancel();
        _debounceTimer.Stop();
        if (IsPflPlaying)
            _ = _api.StopPflAsync();
    }
}
