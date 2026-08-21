using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

public sealed class TracksManagerItemViewModel
{
    public string  AssetId           { get; }
    public string  Title             { get; }
    public string? Artist            { get; }
    public string? Album             { get; }
    public string  AssetType         { get; }
    public string? FormatName        { get; }
    public string? SubcategoryName   { get; }
    public string? Genre             { get; }
    public string  BpmFormatted      { get; }
    public string  DurationFormatted { get; }
    public string  Intro             { get; }
    public int?    Year              { get; }
    public uint    PlayCount         { get; }
    public string  EnabledText        { get; }
    public string  Status            { get; }
    public string? Mood              { get; }
    public string? Gender            { get; }
    public string? Language          { get; }
    public byte?   Rating            { get; }
    public string  DateAdded         { get; }
    public string  RowBackground     { get; }

    public TracksManagerItemViewModel(AssetDto dto)
    {
        AssetId           = dto.AssetId;
        Title             = dto.Title;
        Artist            = dto.Artist;
        Album             = dto.Album;
        AssetType         = dto.AssetType;
        FormatName        = dto.FormatName;
        SubcategoryName   = dto.SubcategoryName;
        Genre             = dto.Genre;
        BpmFormatted      = dto.Bpm is > 0 ? ((int)dto.Bpm.Value).ToString() : "";
        DurationFormatted = FormatMs(dto.DurationMs);
        Intro             = FormatIntro(dto.CueMarkers);
        Year              = dto.Year;
        PlayCount         = dto.PlayCount;
        Status            = dto.Status;
        EnabledText       = dto.Status switch
        {
            "ACTIVE"         => Localizer.Instance?["tm.enabled.yes"]     ?? "Yes",
            "DISABLED"       => Localizer.Instance?["tm.enabled.no"]      ?? "No",
            "PENDING_REVIEW" => Localizer.Instance?["tm.enabled.pending"] ?? "Pending",
            _                => dto.Status
        };
        Mood              = dto.Mood;
        Gender            = dto.Gender;
        Language          = dto.Language;
        Rating            = dto.Rating;
        DateAdded         = dto.CreatedAt.ToLocalTime().ToString("d.MM.yyyy");

        RowBackground = dto.Status switch
        {
            "DISABLED"       => "#2A0000",
            "PENDING_REVIEW" => "#1A1A00",
            _                => "Transparent"
        };
    }

    private static string FormatMs(uint ms)
    {
        if (ms == 0) return "";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalMinutes >= 60 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static string FormatIntro(CueMarkersDto? cueMarkers)
    {
        if (cueMarkers?.Intro is not double intro) return "00:00:00";
        return TimeSpan.FromSeconds(intro).ToString(@"hh\:mm\:ss");
    }
}

public sealed partial class TracksManagerViewModel : ObservableObject, IDisposable
{
    private readonly ApiClientService                _api;
    private readonly INavigationService              _navigation;
    private readonly IAudioSettingsRepository         _settingsRepo;
    private readonly StudioContext                    _studioContext;
    private readonly ILogger<TracksManagerViewModel> _logger;
    private readonly IConfiguration                  _config;
    private readonly IEventBus                       _eventBus;

    private CancellationTokenSource? _searchCts;
    private readonly DispatcherTimer  _debounceTimer;

    // ── Formats (category) list ───────────────────────────────────────────────

    public ObservableCollection<AssetFormatDto> Formats { get; } = new();

    // ── Top filter bar ────────────────────────────────────────────────────────

    public IReadOnlyList<string> StatusFilters { get; } =
        ["Show All", "ACTIVE", "DISABLED", "PENDING_REVIEW"];

    public IReadOnlyList<string> TypeFilters { get; } =
        ["Any Track Type", "TRACK", "CART", "SWEEPER", "VOICETRACK"];

    public ObservableCollection<string>          GenreFilters       { get; } = new(["Any Genre"]);
    public ObservableCollection<SubcategoryDto>  SubcategoryFilters { get; } = new();

    [ObservableProperty] private string                _searchText    = "";
    [ObservableProperty] private AssetFormatDto?       _selectedFormat;
    [ObservableProperty] private string                _selectedStatus = "Show All";
    [ObservableProperty] private string                _selectedType   = "Any Track Type";
    [ObservableProperty] private string                _selectedGenre  = "Any Genre";
    [ObservableProperty] private SubcategoryDto?       _selectedSubcategoryFilter;

    // ── Grid items ────────────────────────────────────────────────────────────

    public ObservableCollection<TracksManagerItemViewModel> Items { get; } = new();

    // Raised after Items are repopulated — code-behind scrolls DataGrid to the anchor item.
    public event Action<TracksManagerItemViewModel>? ScrollToItemRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PflPlayCommand))]
    private TracksManagerItemViewModel? _selectedItem;
    private List<TracksManagerItemViewModel> _selectedItems = new();

    // ── PFL player state ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PflStopCommand))]
    private bool _isPflPlaying;

    [ObservableProperty] private string _pflTrackTitle = "";

    // ── Pagination ────────────────────────────────────────────────────────────

    private int PageSize => _config.GetValue("general:library_page_size", 50);
    private int _totalCount;
    private int _pageOffset;

    [ObservableProperty] private string _paginationText = "";

    public bool HasPreviousPage => _pageOffset > 0;
    public bool HasNextPage     => _pageOffset + Items.Count < _totalCount;

    // ── Status / import ───────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private bool   _isImporting;
    [ObservableProperty] private string _statusMessage  = "";
    [ObservableProperty] private int    _importProgress;
    [ObservableProperty] private int    _importTotal;

    private string? _sortColumn;
    private bool    _sortAscending = true;

    // ── Left panel — Track Analyzer ───────────────────────────────────────────
    // Pre-filled from the Auto DJ → Silence Remover thresholds on Activate(); no hardcoded
    // fallbacks. Cue analysis runs only when Silence Remover is enabled in settings.
    // The user can still override each value in the analyzer panel before running it.
    [ObservableProperty] private bool   _analyzerSilenceRemoverEnabled;
    [ObservableProperty] private double _analyzerStartLevel;
    [ObservableProperty] private double _analyzerNextStartLevel;
    [ObservableProperty] private double _analyzerEndLevel;

    // ── Left panel — batch edit ───────────────────────────────────────────────

    public IReadOnlyList<string> BatchStatusOptions    { get; } =
        ["ACTIVE", "DISABLED", "PENDING_REVIEW"];

    public IReadOnlyList<string> BatchAssetTypeOptions { get; } = ["Track", "Sweeper"];

    public IReadOnlyList<string> BatchMoodOptions { get; } =
        ["Not Set", "Happy", "Sad", "Energetic", "Calm", "Romantic", "Dark", "Upbeat"];

    public IReadOnlyList<string> BatchGenderOptions { get; } =
        ["Not Set", "Male", "Female", "Both", "Instrumental"];

    public IReadOnlyList<string> BatchLanguageOptions { get; } =
        ["Not Set", "Polish", "English", "German", "French", "Spanish", "Italian", "Russian", "Other"];

    public ObservableCollection<string> BatchGenreOptions { get; } = new(["Not Set"]);

    public IReadOnlyList<string> BatchRatingOptions { get; } =
        ["1", "2", "3", "4", "5"];

    public IReadOnlyList<string> TitleOperations { get; } =
        ["Capitalize", "UPPERCASE", "lowercase", "Trim Spaces"];

    public ObservableCollection<SubcategoryDto> BatchSubcategoryOptions { get; } = new();

    [ObservableProperty] private AssetFormatDto?  _batchFormat;
    [ObservableProperty] private SubcategoryDto?  _batchSubcategory;
    [ObservableProperty] private string           _batchStatus    = "ACTIVE";
    [ObservableProperty] private string           _batchAssetType = "Track";
    [ObservableProperty] private string           _batchMood     = "Not Set";
    [ObservableProperty] private string           _batchGender   = "Not Set";
    [ObservableProperty] private string           _batchLanguage = "Not Set";
    [ObservableProperty] private string           _batchGenre    = "Not Set";
    [ObservableProperty] private string           _batchRating   = "1";
    [ObservableProperty] private DateTime?        _batchStartDate;
    [ObservableProperty] private DateTime?        _batchEndDate;

    // ── Left panel — title operations ─────────────────────────────────────────

    [ObservableProperty] private string _titleOperation = "Capitalize";
    [ObservableProperty] private string _replaceFrom    = "";
    [ObservableProperty] private string _replaceTo      = "";

    // ── Constructor ───────────────────────────────────────────────────────────

    public TracksManagerViewModel(
        ApiClientService                api,
        INavigationService              navigation,
        IAudioSettingsRepository        settingsRepo,
        StudioContext                   studioContext,
        IConfiguration                  config,
        IEventBus                       eventBus,
        ILogger<TracksManagerViewModel> logger)
    {
        _api           = api;
        _navigation    = navigation;
        _settingsRepo  = settingsRepo;
        _studioContext = studioContext;
        _config        = config;
        _eventBus      = eventBus;
        _logger        = logger;

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _ = SearchAsync();
        };
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Activate()
    {
        _api.WaveformReady += OnWaveformReady;
        _api.AssetImported += OnAssetImported;
        _ = Task.WhenAll(LoadFormatsAsync(), LoadGenreFiltersAsync(), SearchAsync(), LoadAnalyzerDefaultsAsync());
    }

    /// Pre-fills the Track Analyzer thresholds from the Auto DJ → Silence Remover settings
    /// (Start / Mix = Next Start / End), so they match the studio's configured values.
    /// The user can still adjust them per run.
    private async Task LoadAnalyzerDefaultsAsync()
    {
        try
        {
            var settings = await _settingsRepo.GetByStudioAsync(_studioContext.StudioId);
            if (settings is null) return;

            AnalyzerSilenceRemoverEnabled = settings.SilenceRemoverEnabled;
            AnalyzerStartLevel     = (double)settings.SilenceStartThresholdDb;
            AnalyzerNextStartLevel = (double)settings.SilenceMixThresholdDb;
            AnalyzerEndLevel       = (double)settings.SilenceEndThresholdDb;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load analyzer defaults failed"); }
    }

    // ── Property change triggers ──────────────────────────────────────────────

    partial void OnSearchTextChanged(string value)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    partial void OnSelectedFormatChanged(AssetFormatDto? value)
    {
        _ = LoadSubcategoryFiltersAsync(value?.FormatId);
        _ = ResetAndSearchAsync();
    }

    partial void OnBatchFormatChanged(AssetFormatDto? value)
        => _ = LoadBatchSubcategoriesAsync(value?.FormatId);

    private async Task LoadBatchSubcategoriesAsync(string? formatId)
    {
        BatchSubcategoryOptions.Clear();
        BatchSubcategory = null;
        if (formatId is null) return;
        try
        {
            var envelope = await _api.GetSubcategoriesAsync(formatId);
            if (envelope is not null)
                foreach (var s in envelope.Items)
                    BatchSubcategoryOptions.Add(s);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load batch subcategories failed"); }
    }

    partial void OnSelectedStatusChanged(string value)                     => _ = ResetAndSearchAsync();
    partial void OnSelectedTypeChanged(string value)                       => _ = ResetAndSearchAsync();
    partial void OnSelectedGenreChanged(string value)                      => _ = ResetAndSearchAsync();
    partial void OnSelectedSubcategoryFilterChanged(SubcategoryDto? value) => _ = ResetAndSearchAsync();

    private async Task LoadSubcategoryFiltersAsync(string? formatId)
    {
        SubcategoryFilters.Clear();
        SelectedSubcategoryFilter = null;
        if (formatId is null) return;
        try
        {
            var envelope = await _api.GetSubcategoriesAsync(formatId);
            if (envelope is not null)
                foreach (var s in envelope.Items)
                    SubcategoryFilters.Add(s);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load subcategory filters failed"); }
    }

    // ── Selection (called from code-behind) ───────────────────────────────────

    public void UpdateSelection(IEnumerable<TracksManagerItemViewModel> items)
    {
        _selectedItems = items.ToList();
        OnPropertyChanged(nameof(HasSelection));
    }

    public bool HasSelection  => _selectedItems.Count > 0;
    public int  SelectionCount => _selectedItems.Count;

    // ── Commands — Search / Pagination ───────────────────────────────────────

    [RelayCommand]
    private async Task SearchAsync(string? anchorAssetId = null)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsBusy = true;
        try
        {
            var statusParam = SelectedStatus == "Show All" ? null : SelectedStatus;
            var typeParam   = SelectedType   == "Any Track Type" ? null : SelectedType;
            var genreParam  = SelectedGenre  == "Any Genre"      ? null : SelectedGenre;

            var envelope = await _api.SearchAssetsAsync(
                q:             string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                status:        statusParam,
                assetType:     typeParam,
                formatId:      SelectedFormat?.FormatId,
                genre:         genreParam,
                subcategoryId: SelectedSubcategoryFilter?.SubcategoryId,
                limit:         PageSize,
                offset:        _pageOffset,
                sortColumn:    _sortColumn,
                sortAscending: _sortAscending,
                ct:            ct);

            if (ct.IsCancellationRequested) return;
            if (envelope is null) return;

            Items.Clear();
            foreach (var dto in envelope.Items)
                Items.Add(new TracksManagerItemViewModel(dto));

            _totalCount = envelope.Total;
            UpdatePaginationText();
            OnPropertyChanged(nameof(HasPreviousPage));
            OnPropertyChanged(nameof(HasNextPage));

            if (anchorAssetId is not null)
            {
                var anchor = Items.FirstOrDefault(i => i.AssetId == anchorAssetId);
                if (anchor is not null)
                    ScrollToItemRequested?.Invoke(anchor);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Tracks search failed"); }
        finally { IsBusy = false; }
    }

    private async Task ResetAndSearchAsync()
    {
        _pageOffset = 0;
        await SearchAsync();
    }

    public void SortBy(string col, bool ascending)
    {
        _sortColumn    = col;
        _sortAscending = ascending;
        _pageOffset    = 0;
        _ = SearchAsync();
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

    private void UpdatePaginationText()
    {
        int from        = _totalCount == 0 ? 0 : _pageOffset + 1;
        int to          = Math.Min(_pageOffset + Items.Count, _totalCount);
        int currentPage = _totalCount == 0 ? 0 : _pageOffset / PageSize + 1;
        int totalPages  = (_totalCount + PageSize - 1) / PageSize;
        PaginationText  = _totalCount == 0
            ? Localizer.Instance?["tm.msg.no_results"] ?? "No results"
            : string.Format(
                Localizer.Instance?["tm.pagination_fmt"] ?? "p. {0}/{1}   ({2}–{3} of {4})",
                currentPage, totalPages, from, to, _totalCount);
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    // ── Commands — Track Editor ───────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenTrackEditorAsync()
    {
        if (SelectedItem is null) return;
        var openedAnchor = SelectedItem.AssetId;
        var ids = Items.Select(i => i.AssetId).ToList();
        var idx = ids.IndexOf(openedAnchor);
        var ctx = new TrackEditorNavContext(ids, Math.Max(0, idx));
        var window = await _navigation.ShowModalAsync<TrackEditorWindow>(ctx);

        // Keep the grid where the operator was instead of snapping back to the top: anchor the
        // refresh to the track the editor closed on (they may have paged through several inside it),
        // falling back to the one they opened. SearchAsync(anchor) re-selects + scrolls to it.
        var editedAnchor = (window.DataContext as TrackEditorViewModel)?.AssetId;
        await SearchAsync(string.IsNullOrEmpty(editedAnchor) ? openedAnchor : editedAnchor);
    }

    private string FieldChangedMessage(string labelKey, string labelFallback, string? value)
    {
        var label = Localizer.Instance?[labelKey] ?? labelFallback;
        return string.Format(
            Localizer.Instance?["tm.msg.field_changed"] ?? "{0} → {1} ({2} tracks)",
            label, value, _selectedItems.Count);
    }

    // ── Commands — Batch edit (CHANGE buttons) ────────────────────────────────

    [RelayCommand]
    private async Task ChangeCategoryAsync()
    {
        if (BatchFormat is null || _selectedItems.Count == 0) return;
        await PatchSelectedAsync(detail => BuildUpdateRequest(detail, formatId: BatchFormat.FormatId));
        StatusMessage = FieldChangedMessage("tm.col.category", "Category", BatchFormat.Name);
    }

    [RelayCommand]
    private async Task ChangeSubcategoryAsync()
    {
        if (_selectedItems.Count == 0) return;
        var sub = BatchSubcategory;
        await PatchSelectedAsync(detail => BuildUpdateRequest(
            detail,
            subcategoryId:      sub?.SubcategoryId,
            clearSubcategoryId: sub is null));
        var label = sub?.Name ?? Localizer.Instance?["tm.msg.cleared_label"] ?? "— (cleared)";
        StatusMessage = FieldChangedMessage("tm.col.subcategory", "Subcategory", label);
    }

    [RelayCommand]
    private async Task ChangeStatusAsync()
    {
        if (_selectedItems.Count == 0) return;
        var anchor = _selectedItems[0].AssetId;
        IsBusy = true;
        try
        {
            foreach (var item in _selectedItems.ToList())
                await _api.PatchAssetStatusAsync(item.AssetId, BatchStatus);
            StatusMessage = FieldChangedMessage("tm.lbl.status", "Status", BatchStatus);
            await SearchAsync(anchor);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Batch status change failed"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ChangeAssetTypeAsync()
    {
        if (_selectedItems.Count == 0) return;
        var assetType = BatchAssetType;
        await PatchSelectedAsync(detail => BuildUpdateRequest(detail, assetType: assetType));
        StatusMessage = FieldChangedMessage("tm.col.type", "Type", assetType);
    }

    [RelayCommand]
    private async Task ChangeMoodAsync()
    {
        if (_selectedItems.Count == 0) return;
        var mood = BatchMood == "Not Set" ? null : BatchMood;
        await PatchSelectedAsync(detail => BuildUpdateRequest(detail, mood: mood));
        StatusMessage = FieldChangedMessage("tm.col.mood", "Mood", BatchMood);
    }

    [RelayCommand]
    private async Task ChangeGenderAsync()
    {
        if (_selectedItems.Count == 0) return;
        var gender = BatchGender == "Not Set" ? null : BatchGender;
        await PatchSelectedAsync(detail => BuildUpdateRequest(detail, gender: gender));
        StatusMessage = FieldChangedMessage("tm.col.gender", "Gender", BatchGender);
    }

    [RelayCommand]
    private async Task ChangeLanguageAsync()
    {
        if (_selectedItems.Count == 0) return;
        var lang = BatchLanguage == "Not Set" ? null : BatchLanguage;
        await PatchSelectedAsync(detail => BuildUpdateRequest(detail, language: lang));
        StatusMessage = FieldChangedMessage("tm.col.language", "Language", BatchLanguage);
    }

    [RelayCommand]
    private async Task ChangeGenreAsync()
    {
        if (_selectedItems.Count == 0) return;
        var genre = BatchGenre == "Not Set" ? null : BatchGenre;
        await PatchSelectedAsync(detail => BuildUpdateRequest(detail, genre: genre));
        StatusMessage = FieldChangedMessage("tm.col.genre", "Genre", BatchGenre);
    }

    [RelayCommand]
    private async Task ChangeRatingAsync()
    {
        if (_selectedItems.Count == 0) return;
        byte.TryParse(BatchRating, out var rating);
        await PatchSelectedAsync(detail => BuildUpdateRequest(detail, rating: rating));
        StatusMessage = FieldChangedMessage("tm.col.rating", "Rating", BatchRating);
    }

    [RelayCommand]
    private async Task ChangeDateAsync()
    {
        if (_selectedItems.Count == 0) return;
        await PatchSelectedAsync(detail => BuildUpdateRequest(detail, startDate: BatchStartDate, endDate: BatchEndDate));
        StatusMessage = string.Format(
            Localizer.Instance?["tm.msg.date_changed"] ?? "{0} → {1} tracks updated",
            Localizer.Instance?["tm.lbl.date"] ?? "Date", _selectedItems.Count);
    }

    // ── Commands — Title Operations ───────────────────────────────────────────

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (_selectedItems.Count == 0) return;
        await PatchSelectedAsync(detail =>
        {
            var newTitle = TitleOperation switch
            {
                "UPPERCASE"   => detail.Title.ToUpperInvariant(),
                "lowercase"   => detail.Title.ToLowerInvariant(),
                "Trim Spaces" => detail.Title.Trim(),
                _             => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(detail.Title.ToLowerInvariant()),
            };
            return BuildUpdateRequest(detail, title: newTitle);
        });
        StatusMessage = string.Format(
            Localizer.Instance?["tm.msg.renamed"] ?? "Renamed ({0}): {1} tracks",
            TitleOperation, _selectedItems.Count);
    }

    [RelayCommand]
    private async Task ReplaceAsync()
    {
        if (_selectedItems.Count == 0 || string.IsNullOrEmpty(ReplaceFrom)) return;
        await PatchSelectedAsync(detail =>
        {
            var newTitle = detail.Title.Replace(ReplaceFrom, ReplaceTo, StringComparison.Ordinal);
            return BuildUpdateRequest(detail, title: newTitle);
        });
        StatusMessage = string.Format(
            Localizer.Instance?["tm.msg.replaced"] ?? "Replaced '{0}' → '{1}': {2} tracks",
            ReplaceFrom, ReplaceTo, _selectedItems.Count);
    }

    // ── Commands — Track Analyzer ─────────────────────────────────────────────

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (_selectedItems.Count == 0) return;
        IsBusy = true;
        try
        {
            var ids = _selectedItems.Select(i => i.AssetId).ToList();
            await _api.AnalyzeLoudnessAsync(ids);
            StatusMessage = string.Format(
                Localizer.Instance?["tm.msg.loudness_queued"] ?? "Loudness analysis queued for {0} tracks", ids.Count);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Analyze failed"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AnalyzeCueAsync()
    {
        if (_selectedItems.Count == 0) return;
        if (!AnalyzerSilenceRemoverEnabled)
        {
            StatusMessage = Localizer.Instance?["tm.msg.silence_remover_needed"] ?? "Enable Silence Remover in Settings to analyze cue points.";
            return;
        }
        IsBusy = true;
        try
        {
            var ids = _selectedItems.Select(i => i.AssetId).ToList();
            await _api.AnalyzeCueAsync(ids, AnalyzerStartLevel, AnalyzerNextStartLevel, AnalyzerEndLevel);
            StatusMessage = string.Format(
                Localizer.Instance?["tm.msg.cue_queued"] ?? "Cue analysis queued for {0} tracks", ids.Count);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Cue analyze failed"); }
        finally { IsBusy = false; }
    }

    // ── Commands — Maintenance ────────────────────────────────────────────────

    public async Task DeleteSelectedAsync(bool deleteFile)
    {
        if (_selectedItems.Count == 0) return;
        IsBusy = true;
        var ids = _selectedItems.Select(x => x.AssetId).ToList();
        try
        {
            var result = await _api.BatchDeleteAssetsAsync(ids, deleteFile);
            int deleted = result?.Deleted ?? ids.Count;
            StatusMessage = deleteFile
                ? string.Format(Localizer.Instance?["tm.msg.deleted_db_disk"] ?? "Deleted {0} tracks from the database and disk.", deleted)
                : string.Format(Localizer.Instance?["tm.msg.deleted_db"]      ?? "Deleted {0} tracks from the database.", deleted);
            await SearchAsync();
            await _eventBus.PublishAsync(new AssetLibraryChangedEvent());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeleteSelectedAsync failed (deleteFile={Del})", deleteFile);
            StatusMessage = Localizer.Instance?["tm.msg.delete_error"] ?? "Error while deleting.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task PurgeOrphansAsync()
    {
        IsBusy = true;
        StatusMessage = Localizer.Instance?["tm.msg.searching_missing"] ?? "Searching for missing files…";
        try
        {
            var result = await _api.PurgeOrphansAsync();
            if (result is null)
            {
                StatusMessage = Localizer.Instance?["tm.msg.purge_error"] ?? "Error while removing orphaned records.";
                return;
            }
            StatusMessage = result.Deleted == 0
                ? Localizer.Instance?["tm.msg.purge_none"] ?? "No orphaned records — the database is clean."
                : string.Format(Localizer.Instance?["tm.msg.purge_deleted"] ?? "Deleted {0} records with no file on disk.", result.Deleted);
            if (result.Deleted > 0)
                await SearchAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Purge orphans failed"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task OptimizeDatabaseAsync()
    {
        IsBusy = true;
        StatusMessage = Localizer.Instance?["tm.msg.optimizing"] ?? "Optimizing database…";
        try
        {
            var result = await _api.OptimizeDatabaseAsync();
            StatusMessage = result is null
                ? Localizer.Instance?["tm.msg.optimize_error"] ?? "Database optimization error."
                : string.Format(Localizer.Instance?["tm.msg.optimized"] ?? "Database optimized ({0} ms).", result.DurationMs);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Optimize DB failed"); }
        finally { IsBusy = false; }
    }

    // ── Commands — Import ─────────────────────────────────────────────────────

    public async Task<ImportRunReport> ImportFilesAsync(
        IReadOnlyList<string> paths,
        DirectoryImportSettings settings,
        IProgress<(int done, int total)>? externalProgress = null,
        CancellationToken ct = default)
    {
        if (paths.Count == 0) return ImportRunReport.Empty;
        IsImporting    = true;
        ImportProgress = 0;
        ImportTotal    = paths.Count;
        StatusMessage  = string.Format(Localizer.Instance?["tm.msg.importing_start"] ?? "Importing 0 / {0}…", paths.Count);

        int total      = paths.Count;
        int completed  = 0;
        int duplicates = 0;
        int errors     = 0;
        var assetIds   = new List<string>();

        try
        {
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();

                var resp = await _api.StartImportAsync(new ImportRequestDto(
                    path, settings.AssetType, settings.CategoryFormatId, settings.SubcategoryId,
                    ReadRdm:  settings.ReadRdm,
                    ReadMmd:  settings.ReadMmd,
                    ReadWfrm: settings.ReadWfrm,
                    ReadId3:  settings.ReadId3), ct);

                if (resp is null)
                {
                    // Import never started (server/HTTP error) — count as error, not skip.
                    errors++;
                }
                else
                {
                    // Poll this single import until completion (per-file timeout 60s).
                    var deadline  = DateTime.UtcNow.AddSeconds(60);
                    var completedThisFile = false;
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(150, ct);
                        var status = await _api.GetImportStatusAsync(resp.ImportId, ct);
                        if (status?.Status is "COMPLETED" or "FAILED")
                        {
                            completedThisFile = true;
                            if (status.Status == "COMPLETED" && status.AssetId is not null)
                            {
                                if (status.IsDuplicate)
                                    duplicates++;
                                else
                                    assetIds.Add(status.AssetId);
                            }
                            else
                            {
                                // FAILED, or COMPLETED without an asset id.
                                errors++;
                            }
                            break;
                        }
                    }

                    if (!completedThisFile)
                        errors++; // per-file timeout
                }

                completed++;
                ImportProgress = completed;
                var dupInfo = duplicates > 0
                    ? string.Format(Localizer.Instance?["tm.msg.skipped_suffix"] ?? ", {0} skipped", duplicates)
                    : "";
                StatusMessage = string.Format(
                    Localizer.Instance?["tm.msg.importing_progress"] ?? "Importing {0} / {1}…{2}",
                    completed, total, dupInfo);
                externalProgress?.Report((completed, total));
            }

            if (assetIds.Count > 0)
                await ApplyPostImportSettingsAsync(assetIds, settings, ct);

            await SearchAsync();
            if (assetIds.Count > 0)
                await _eventBus.PublishAsync(new AssetLibraryChangedEvent());
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Import cancelled after {Completed}/{Total} files", completed, total);
            // Refresh so already-imported assets appear; surface a partial report below.
            try { await SearchAsync(); } catch { /* refresh is best-effort on cancel */ }
            if (assetIds.Count > 0)
                try { await _eventBus.PublishAsync(new AssetLibraryChangedEvent()); } catch { /* best-effort */ }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Import failed"); }
        finally
        {
            IsImporting = false;
        }

        return new ImportRunReport(assetIds.Count, duplicates, errors);
    }

    private async Task ApplyPostImportSettingsAsync(
        IReadOnlyList<string> assetIds, DirectoryImportSettings settings, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "ApplyPostImport: {Count} assets | FormatId={FormatId} SubcategoryId={SubcategoryId} " +
            "AutoGenre={AutoGenre} SelectedGenreId={GenreId} TrackStatus={Status}",
            assetIds.Count,
            settings.CategoryFormatId ?? "(null)",
            settings.SubcategoryId    ?? "(null)",
            settings.AutoGenre,
            settings.SelectedGenreId  ?? "(null)",
            settings.TrackStatus);

        // Track status — dedicated PATCH, no full read-modify-write needed
        if (settings.TrackStatus != "ACTIVE")
        {
            foreach (var id in assetIds)
            {
                ct.ThrowIfCancellationRequested();
                try { await _api.PatchAssetStatusAsync(id, settings.TrackStatus, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Status patch failed for {Id}", id); }
            }
        }

        // Category, Subcategory, StartDate, EndDate, PlayLimit, Genre override require read-modify-write
        bool needsDetailUpdate = !string.IsNullOrWhiteSpace(settings.CategoryFormatId)
                              || !string.IsNullOrWhiteSpace(settings.SubcategoryId)
                              || settings.StartDate.HasValue
                              || settings.EndDate.HasValue
                              || settings.PlayTrackForTimes > 0
                              || (!settings.AutoGenre && !string.IsNullOrWhiteSpace(settings.SelectedGenreId));

        _logger.LogInformation("ApplyPostImport: needsDetailUpdate={Needs}", needsDetailUpdate);

        if (needsDetailUpdate)
        {
            foreach (var id in assetIds)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var d = await _api.GetAssetDetailAsync(id, ct);
                    if (d is null)
                    {
                        _logger.LogWarning("ApplyPostImport: GetAssetDetail returned null for {Id}", id);
                        continue;
                    }

                    var formatId = !string.IsNullOrWhiteSpace(settings.CategoryFormatId)
                                   ? settings.CategoryFormatId : d.FormatId;
                    var subcatId = !string.IsNullOrWhiteSpace(settings.SubcategoryId)
                                   ? settings.SubcategoryId : d.SubcategoryId;
                    var genre    = !settings.AutoGenre && !string.IsNullOrWhiteSpace(settings.SelectedGenreId)
                                   ? settings.SelectedGenreId : d.Genre;

                    _logger.LogInformation(
                        "ApplyPostImport: updating {Id} → FormatId={FormatId} SubcategoryId={SubcatId} Genre={Genre}",
                        id, formatId ?? "(null)", subcatId ?? "(null)", genre ?? "(null)");

                    var req = new UpdateAssetRequestDto(
                        Title:         d.Title,
                        Artist:        d.Artist,
                        Album:         d.Album,
                        FormatId:      formatId,
                        SubcategoryId: subcatId,
                        Bpm:           d.Bpm,
                        Year:          d.Year,
                        Rating:        d.Rating,
                        Mood:          d.Mood,
                        Gender:        d.Gender,
                        Language:      d.Language,
                        Genre:         genre,
                        Comments:      d.Comments,
                        Status:        d.Status,
                        StartDate:     settings.StartDate ?? d.StartDate,
                        EndDate:       settings.EndDate   ?? d.EndDate,
                        PlayLimit:     settings.PlayTrackForTimes > 0
                                       ? (uint?)settings.PlayTrackForTimes : d.PlayLimit,
                        CueMarkers:    d.CueMarkers);

                    await _api.UpdateAssetAsync(id, req, ct);
                    _logger.LogInformation("ApplyPostImport: UpdateAsset OK for {Id}", id);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Post-import update failed for {Id}", id); }
            }
        }

        // Silence-based cue detection happens in the import pipeline (AutoCueDetector,
        // gated on Silence Remover) and only fills empty cues — it never overwrites
        // values from .rdm / .wfrm. No separate post-import cue pass here.

        // Auto BPM — BPM from ID3 tag is already read by ImportPipeline (Id3TagReader).
        // Audio-based BPM analysis (BpmQueue) is not yet implemented (Variant B).
        if (settings.AutoDetectBpm)
            _logger.LogDebug("Auto BPM: ID3 BPM imported; audio analysis not yet available");
    }

    // ── Commands — PFL player ─────────────────────────────────────────────────

    private bool CanPflPlay() => SelectedItem is not null;
    private bool CanPflStop() => IsPflPlaying;

    [RelayCommand(CanExecute = nameof(CanPflPlay))]
    private async Task PflPlayAsync()
    {
        if (SelectedItem is null) return;
        try
        {
            await _api.StartPflAsync(SelectedItem.AssetId);
            IsPflPlaying  = true;
            PflTrackTitle = SelectedItem.Title;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PFL start failed for {AssetId}", SelectedItem.AssetId);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPflStop))]
    private async Task PflStopAsync()
    {
        try
        {
            await _api.StopPflAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PFL stop failed");
        }
        finally
        {
            IsPflPlaying  = false;
            PflTrackTitle = "";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public Task ReloadFormatsAsync() => LoadFormatsAsync();

    private async Task LoadFormatsAsync()
    {
        try
        {
            var env = await _api.GetFormatsAsync();
            if (env is null) return;
            Formats.Clear();
            foreach (var f in env.Items)
                Formats.Add(f);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load formats failed"); }
    }

    private async Task LoadGenreFiltersAsync()
    {
        try
        {
            var envelope = await _api.GetGenresAsync();
            if (envelope is null) return;

            GenreFilters.Clear();
            GenreFilters.Add("Any Genre");

            BatchGenreOptions.Clear();
            BatchGenreOptions.Add("Not Set");

            foreach (var g in envelope.Items)
            {
                GenreFilters.Add(g.Name);
                BatchGenreOptions.Add(g.Name);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load genre filters failed"); }
    }

    private async Task PatchSelectedAsync(Func<AssetDetailDto, UpdateAssetRequestDto> buildRequest)
    {
        var ids = _selectedItems.Select(i => i.AssetId).ToList();
        if (ids.Count == 0) return;

        var anchor = _selectedItems[0].AssetId;
        IsBusy = true;
        try
        {
            foreach (var id in ids)
            {
                var detail = await _api.GetAssetDetailAsync(id);
                if (detail is null) continue;
                var req = buildRequest(detail);
                await _api.UpdateAssetAsync(id, req);
            }
            await SearchAsync(anchor);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Batch patch failed"); }
        finally { IsBusy = false; }
    }

    private static UpdateAssetRequestDto BuildUpdateRequest(
        AssetDetailDto detail,
        string?   title         = null,
        string?   formatId      = null,
        string?   subcategoryId = null,
        bool      clearSubcategoryId = false,
        string?   mood          = null,
        string?   gender        = null,
        string?   language      = null,
        string?   genre         = null,
        byte?     rating        = null,
        DateTime? startDate     = null,
        DateTime? endDate       = null,
        string?   assetType     = null)
    {
        return new UpdateAssetRequestDto(
            Title:         title    ?? detail.Title,
            Artist:        detail.Artist,
            Album:         detail.Album,
            FormatId:      formatId ?? detail.FormatId,
            SubcategoryId: clearSubcategoryId ? null : (subcategoryId ?? detail.SubcategoryId),
            Bpm:           detail.Bpm,
            Year:          detail.Year,
            Rating:        rating   ?? detail.Rating,
            Mood:          mood     ?? detail.Mood,
            Gender:        gender   ?? detail.Gender,
            Language:      language ?? detail.Language,
            Genre:         genre    ?? detail.Genre,
            Comments:      detail.Comments,
            Status:        detail.Status,
            StartDate:     startDate ?? detail.StartDate,
            EndDate:       endDate   ?? detail.EndDate,
            PlayLimit:     detail.PlayLimit,
            AssetType:     assetType,
            CueMarkers:    detail.CueMarkers
        );
    }

    // ── WebSocket handlers ────────────────────────────────────────────────────

    private void OnWaveformReady(WaveformReadyPayload payload)
    {
        // A waveform finishing (e.g. after an edit re-analyses the track) must not yank the grid to
        // the top — anchor the refresh to the current selection so the view stays put.
        if (Items.Any(i => i.AssetId == payload.AssetId))
            Dispatcher.UIThread.Post(() => _ = SearchAsync(SelectedItem?.AssetId));
    }

    private void OnAssetImported(AssetImportedPayload payload)
    {
        Dispatcher.UIThread.Post(() => _ = SearchAsync(SelectedItem?.AssetId));
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _api.WaveformReady -= OnWaveformReady;
        _api.AssetImported -= OnAssetImported;
        _searchCts?.Cancel();
        _debounceTimer.Stop();
        if (IsPflPlaying)
            _ = _api.StopPflAsync();
    }
}

/// Outcome of an import run: how many files were newly imported, skipped as
/// duplicates, and failed. Used to build the end-of-run report (Import Folder,
/// Update Tracks).
public sealed record ImportRunReport(int Imported, int Skipped, int Errors)
{
    public static ImportRunReport Empty { get; } = new(0, 0, 0);
}
