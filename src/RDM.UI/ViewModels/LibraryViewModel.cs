using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Shared.DTOs;
using RDM.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

public sealed class LibraryItemViewModel
{
    public string  AssetId           { get; }
    public string? Artist            { get; }
    public string  Title             { get; }
    public string  IntroFormatted    { get; }
    public uint    IntroMs           { get; }
    public string  DurationFormatted { get; }
    public uint    DurationMs        { get; }
    public decimal? Bpm              { get; }
    public string  BpmFormatted      { get; }
    public string  DateAdded         { get; }
    public DateTime CreatedAt        { get; }
    public bool    IsInternetStream  { get; }
    public string  TypeIcon          { get; }

    public LibraryItemViewModel(AssetDto dto)
    {
        AssetId         = dto.AssetId;
        Artist          = dto.Artist;
        Title           = dto.Title;
        IsInternetStream = dto.AssetType == "InternetStream";
        TypeIcon         = IsInternetStream ? "📡" : "";
        Bpm              = dto.Bpm;
        BpmFormatted     = IsInternetStream ? "LIVE" : (dto.Bpm is > 0 ? ((int)dto.Bpm.Value).ToString() : "");
        DurationMs        = dto.DurationMs;
        DurationFormatted = IsInternetStream ? "LIVE" : FormatDuration(dto.DurationMs);
        CreatedAt = dto.CreatedAt;
        DateAdded = dto.CreatedAt.ToLocalTime().ToString("d.MM.yyyy");

        IntroMs        = IsInternetStream ? 0 : (dto.CueMarkers?.Intro is double intro ? (uint)(intro * 1000) : 0);
        IntroFormatted = IntroMs > 0 ? FormatDuration(IntroMs) : "";
    }

    private static string FormatDuration(uint ms)
    {
        if (ms == 0) return "";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}

public sealed partial class LibraryViewModel : ObservableObject, IDisposable
{
    private int PageSize => _config.GetValue("general:library_page_size", 50);

    private readonly ApiClientService           _api;
    private readonly IConfiguration             _config;
    private readonly INavigationService         _navigation;
    private readonly ILibrarySelectionService   _selection;
    private readonly IInsertCursorService       _cursor;
    private readonly ILogger<LibraryViewModel>  _logger;
    private readonly IEventBus                  _eventBus;

    private CancellationTokenSource? _searchCts;
    private readonly DispatcherTimer  _debounceTimer;

    private int    _totalCount;
    private int    _pageOffset;
    private string _sortColumn    = "Artist";
    private bool   _sortAscending = true;

    // ── Collections ───────────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<LibraryItemViewModel> _items = new();
    public ObservableCollection<AssetFormatDto> Formats            { get; } = new();
    public ObservableCollection<SubcategoryDto> SubcategoryFilters { get; } = new();

    public ObservableCollection<string> GenreFilters { get; } = new(["Any Genre"]);

    // ── Observable state ──────────────────────────────────────────────────────

    [ObservableProperty] private string  _searchText = "";
    [ObservableProperty] private bool    _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToPlaylistCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayPflCommand))]
    private LibraryItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayPflCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopPflCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeekBackwardCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeekForwardCommand))]
    private bool _isPflPlaying;

    [ObservableProperty] private AssetFormatDto?  _selectedFormat;
    [ObservableProperty] private SubcategoryDto?  _selectedSubcategory;
    [ObservableProperty] private string           _selectedGenre  = "Any Genre";
    [ObservableProperty] private string          _paginationText = "0 / 0";

    public bool HasPreviousPage => _pageOffset > 0;
    public bool HasNextPage     => _pageOffset + Items.Count < _totalCount;

    // ── Constructor ───────────────────────────────────────────────────────────

    public LibraryViewModel(
        ApiClientService          api,
        IConfiguration            config,
        INavigationService        navigation,
        ILibrarySelectionService  selection,
        IInsertCursorService      cursor,
        IEventBus                 eventBus,
        ILogger<LibraryViewModel> logger)
    {
        _api        = api;
        _config     = config;
        _navigation = navigation;
        _selection  = selection;
        _cursor     = cursor;
        _eventBus   = eventBus;
        _logger     = logger;

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _ = ResetAndSearchAsync();
        };
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Activate()
    {
        _eventBus.Subscribe<AssetLibraryChangedEvent>(OnLibraryChanged);
        _ = Task.WhenAll(LoadFormatsAsync(), LoadGenreFiltersAsync(), SearchAsync());
    }

    private void OnLibraryChanged(AssetLibraryChangedEvent evt)
        => Dispatcher.UIThread.Post(() => _ = ResetAndSearchAsync());

    // ── Property change triggers ──────────────────────────────────────────────

    partial void OnSelectedItemChanged(LibraryItemViewModel? value)
        => _selection.SetSelection(value?.AssetId);

    partial void OnSearchTextChanged(string value)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    partial void OnSelectedFormatChanged(AssetFormatDto? value)
    {
        _ = LoadSubcategoriesAsync(value?.FormatId);
        _ = ResetAndSearchAsync();
    }

    partial void OnSelectedSubcategoryChanged(SubcategoryDto? value) => _ = ResetAndSearchAsync();
    partial void OnSelectedGenreChanged(string value)                 => _ = ResetAndSearchAsync();

    // ── Commands — Search ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsBusy = true;
        try
        {
            var formatId      = string.IsNullOrEmpty(SelectedFormat?.FormatId)      ? null : SelectedFormat!.FormatId;
            var subcategoryId = string.IsNullOrEmpty(SelectedSubcategory?.SubcategoryId) ? null : SelectedSubcategory!.SubcategoryId;
            var genreParam    = SelectedGenre == "Any Genre" ? null : SelectedGenre;

            var envelope = await _api.SearchAssetsAsync(
                q:             string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                formatId:      formatId,
                subcategoryId: subcategoryId,
                genre:         genreParam,
                limit:         PageSize,
                offset:        _pageOffset,
                ct:            ct);

            if (ct.IsCancellationRequested) return;
            if (envelope is null) return;

            _totalCount = envelope.Total;

            var sorted = SortList(envelope.Items.Select(dto => new LibraryItemViewModel(dto)));
            Items = new ObservableCollection<LibraryItemViewModel>(sorted);

            UpdatePaginationState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Library search failed"); }
        finally { IsBusy = false; }
    }

    private async Task ResetAndSearchAsync()
    {
        _pageOffset = 0;
        await SearchAsync();
    }

    // ── Commands — Pagination ─────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    private async Task FirstPageAsync()
    {
        _pageOffset = 0;
        await SearchAsync();
    }

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    private async Task PreviousPageAsync()
    {
        _pageOffset = Math.Max(0, _pageOffset - PageSize);
        await SearchAsync();
    }

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private async Task NextPageAsync()
    {
        _pageOffset += PageSize;
        await SearchAsync();
    }

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private async Task LastPageAsync()
    {
        if (_totalCount == 0) return;
        int lastPageOffset = ((_totalCount - 1) / PageSize) * PageSize;
        _pageOffset = lastPageOffset;
        await SearchAsync();
    }

    private void UpdatePaginationState()
    {
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        FirstPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
        if (_totalCount == 0 || Items.Count == 0)
            PaginationText = "0 / 0";
        else
            PaginationText = $"{_pageOffset + 1}–{_pageOffset + Items.Count} / {_totalCount}";
    }

    // ── Commands — Playlist ───────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanAddToPlaylist))]
    private async Task AddToPlaylistAsync(CancellationToken ct)
    {
        if (SelectedItem is null) return;
        try
        {
            int position = int.MaxValue;
            if (_cursor.IsActive)
            {
                var envelope = await _api.GetPlaylistItemsAsync(ct);
                int visibleOffset = 0;
                if (envelope?.CurrentItemId is not null)
                {
                    var items = envelope.Items;
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (items[i].ItemId == envelope.CurrentItemId) { visibleOffset = i + 1; break; }
                    }
                }
                position = _cursor.VisibleIndex!.Value + visibleOffset;
                _cursor.Advance();
            }
            await _api.AddPlaylistItemAsync(
                new AddPlaylistItemRequestDto(SelectedItem.AssetId, null, position, "ASSET"), ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Add to playlist failed"); }
    }

    private bool CanAddToPlaylist() => SelectedItem is not null;

    [RelayCommand(CanExecute = nameof(CanAddToPlaylist))]
    private async Task AddNextAsync(CancellationToken ct)
    {
        if (SelectedItem is null) return;
        try
        {
            var envelope = await _api.GetPlaylistItemsAsync(ct);
            int insertIndex = int.MaxValue;
            if (envelope is not null && envelope.CurrentItemId is not null)
            {
                var items    = envelope.Items;
                int onAirIdx = -1;
                for (int i = 0; i < items.Count; i++)
                    if (items[i].ItemId == envelope.CurrentItemId) { onAirIdx = i; break; }
                if (onAirIdx >= 0)
                    insertIndex = onAirIdx + 1;
            }
            await _api.AddPlaylistItemAsync(
                new AddPlaylistItemRequestDto(SelectedItem.AssetId, null, insertIndex, "ASSET"), ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Add Next failed"); }
    }

    // ── Commands — PFL ────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanPlayPfl))]
    private async Task PlayPflAsync(CancellationToken ct)
    {
        if (SelectedItem is null) return;
        try
        {
            await _api.StartPflAsync(SelectedItem.AssetId, ct);
            IsPflPlaying = true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PFL start failed"); }
    }

    private bool CanPlayPfl() => SelectedItem is not null && !IsPflPlaying;

    [RelayCommand(CanExecute = nameof(CanStopPfl))]
    private async Task StopPflAsync(CancellationToken ct)
    {
        try
        {
            await _api.StopPflAsync(ct);
        }
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

    // ── Sorting ───────────────────────────────────────────────────────────────

    public void SortBy(string sortMemberPath, bool ascending)
    {
        _sortColumn    = sortMemberPath;
        _sortAscending = ascending;
        Items = new ObservableCollection<LibraryItemViewModel>(SortList(Items));
    }

    private IEnumerable<LibraryItemViewModel> SortList(IEnumerable<LibraryItemViewModel> source) =>
        (_sortColumn, _sortAscending) switch
        {
            ("Artist",     true)  => source.OrderBy(x => x.Artist ?? ""),
            ("Artist",     false) => source.OrderByDescending(x => x.Artist ?? ""),
            ("Title",      true)  => source.OrderBy(x => x.Title),
            ("Title",      false) => source.OrderByDescending(x => x.Title),
            ("IntroMs",    true)  => source.OrderBy(x => x.IntroMs),
            ("IntroMs",    false) => source.OrderByDescending(x => x.IntroMs),
            ("DurationMs", true)  => source.OrderBy(x => x.DurationMs),
            ("DurationMs", false) => source.OrderByDescending(x => x.DurationMs),
            ("Bpm",        true)  => source.OrderBy(x => x.Bpm ?? 0),
            ("Bpm",        false) => source.OrderByDescending(x => x.Bpm ?? 0),
            ("DateAdded",  true)  => source.OrderBy(x => x.CreatedAt),
            ("DateAdded",  false) => source.OrderByDescending(x => x.CreatedAt),
            _                     => source.OrderBy(x => x.Artist ?? "")
        };

    // ── Context menu — Track Editor ───────────────────────────────────────────

    [RelayCommand]
    private async Task OpenTrackEditorAsync()
    {
        if (SelectedItem is null) return;
        var restoredId = SelectedItem.AssetId;
        var ids = Items.Select(i => i.AssetId).ToList();
        var idx = ids.IndexOf(restoredId);
        var ctx = new TrackEditorNavContext(ids, Math.Max(0, idx));
        await _navigation.ShowModalAsync<RDM.UI.Views.TrackEditorWindow>(ctx);
        await SearchAsync();
        SelectedItem = Items.FirstOrDefault(x => x.AssetId == restoredId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
        _eventBus.Unsubscribe<AssetLibraryChangedEvent>(OnLibraryChanged);
        _searchCts?.Cancel();
        _debounceTimer.Stop();
    }
}
