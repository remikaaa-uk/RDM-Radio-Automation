using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

public sealed partial class TrackImportViewModel : ObservableObject
{
    private readonly ApiClientService              _api;
    private readonly INavigationService            _navigation;
    private readonly IAudioSettingsRepository      _settingsRepo;
    private readonly StudioContext                 _studioContext;
    private readonly ILogger<TrackImportViewModel> _logger;

    private string? _assetId;
    private string? _localFilePath;
    private string? _imagePath;

    // Silence Remover thresholds — loaded from audio settings, never hardcoded.
    private bool   _silenceRemoverEnabled;
    private double _cueStartDb;
    private double _cueNextStartDb;
    private double _cueEndDb;

    // ── Player ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _artist              = "";
    [ObservableProperty] private string _titleText           = "";
    [ObservableProperty] private string _currentPositionText = "00:00:00";
    [ObservableProperty] private string _totalDurationText   = "00:00:00";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isPlaying;

    // ── Settings — path ───────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private string _pathOrUrl = "";

    // ── Details tab — editable metadata ──────────────────────────────────────
    [ObservableProperty] private string   _album = "";
    [ObservableProperty] private decimal? _year;
    [ObservableProperty] private decimal? _bpm;

    // ── Settings — left column ────────────────────────────────────────────────
    [ObservableProperty] private AssetFormatDto?   _selectedCategory;
    [ObservableProperty] private SubcategoryDto?   _selectedSubcategory;
    [ObservableProperty] private string?           _selectedGenre;
    [ObservableProperty] private string            _selectedTrackType = "TRACK";
    [ObservableProperty] private int               _playForTimes;
    [ObservableProperty] private string            _selectedAfterPlay = "Nothing";
    [ObservableProperty] private bool              _startDateEnabled;
    [ObservableProperty] private string            _startDateText     = "";
    [ObservableProperty] private bool              _endDateEnabled;
    [ObservableProperty] private string            _endDateText       = "";

    // ── Settings — right column ───────────────────────────────────────────────
    [ObservableProperty] private bool   _highPrecisionCuePoints = true;
    [ObservableProperty] private bool   _isVariableDuration;
    [ObservableProperty] private bool   _overlayFile;
    [ObservableProperty] private bool   _useOriginalStreamTitle;
    [ObservableProperty] private string _selectedStatus = "ACTIVE";
    [ObservableProperty] private string _trackId        = "";

    // ── Read from ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _readRdm  = true;
    [ObservableProperty] private bool _readMmd  = false;   // .MMD reading disabled
    [ObservableProperty] private bool _readWfrm = true;
    [ObservableProperty] private bool _readId3  = true;

    // ── Image tab ────────────────────────────────────────────────────────────
    [ObservableProperty] private Bitmap? _coverArt;

    partial void OnCoverArtChanging(Bitmap? oldValue, Bitmap? newValue)
        => oldValue?.Dispose();

    // ── Comments tab ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _comments = "";

    // ── Status ────────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private bool _isImporting;

    [ObservableProperty] private string _statusText = "";

    // ── Collections ───────────────────────────────────────────────────────────
    public ObservableCollection<AssetFormatDto>  Formats            { get; } = [];
    public ObservableCollection<SubcategoryDto>  SubcategoryOptions { get; } = [];
    public ObservableCollection<string>          GenreOptions       { get; } = [];

    public IReadOnlyList<string> TrackTypes       { get; } = ["TRACK", "CART", "SWEEPER", "VOICETRACK"];
    public IReadOnlyList<string> AfterPlayOptions { get; } = ["Nothing", "Remove"];
    public IReadOnlyList<string> StatusOptions    { get; } = ["ACTIVE", "DISABLED", "PENDING_REVIEW"];

    // ── Result ────────────────────────────────────────────────────────────────
    public TrackImportResult? Result { get; private set; }

    // ── Constructor ───────────────────────────────────────────────────────────
    public TrackImportViewModel(
        ApiClientService              api,
        INavigationService            navigation,
        IAudioSettingsRepository      settingsRepo,
        StudioContext                 studioContext,
        ILogger<TrackImportViewModel> logger)
    {
        _api           = api;
        _navigation    = navigation;
        _settingsRepo  = settingsRepo;
        _studioContext = studioContext;
        _logger        = logger;

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _startDateText = now;
        _endDateText   = now;
    }

    // ── Init ─────────────────────────────────────────────────────────────────
    public async Task LoadCueLevelsAsync()
    {
        try
        {
            var s = await _settingsRepo.GetByStudioAsync(_studioContext.StudioId);
            if (s is null) return;
            _silenceRemoverEnabled = s.SilenceRemoverEnabled;
            _cueStartDb     = (double)s.SilenceStartThresholdDb;
            _cueNextStartDb = (double)s.SilenceMixThresholdDb;
            _cueEndDb       = (double)s.SilenceEndThresholdDb;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to load cue thresholds"); }
    }

    public async Task LoadFormatsAsync()
    {
        try
        {
            var envelope = await _api.GetFormatsAsync();
            if (envelope is null) return;
            Formats.Clear();
            foreach (var f in envelope.Items)
                Formats.Add(f);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load formats in TrackImportViewModel");
        }
    }

    public async Task LoadGenresAsync()
    {
        try
        {
            var envelope = await _api.GetGenresAsync();
            if (envelope is null) return;
            GenreOptions.Clear();
            foreach (var g in envelope.Items)
                GenreOptions.Add(g.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load genres in TrackImportViewModel");
        }
    }

    partial void OnSelectedCategoryChanged(AssetFormatDto? value)
        => _ = LoadSubcategoriesAsync(value?.FormatId);

    private async Task LoadSubcategoriesAsync(string? formatId)
    {
        SubcategoryOptions.Clear();
        SelectedSubcategory = null;
        if (formatId is null) return;
        try
        {
            var envelope = await _api.GetSubcategoriesAsync(formatId);
            if (envelope is null) return;
            foreach (var s in envelope.Items)
                SubcategoryOptions.Add(s);
            if (SubcategoryOptions.Count > 0)
                SelectedSubcategory = SubcategoryOptions[0];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load subcategories");
        }
    }

    // ── Player commands ───────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync(CancellationToken ct)
    {
        try
        {
            if (_assetId is not null)
                await _api.StartPflAsync(_assetId, ct);
            else if (!string.IsNullOrEmpty(_localFilePath))
                await _api.StartPflFromFileAsync(_localFilePath, ct);
            else return;
            IsPlaying = true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PFL start failed"); }
    }

    private bool CanPlay() => (_assetId is not null || !string.IsNullOrEmpty(_localFilePath)) && !IsPlaying;

    public async Task LoadFileForPreviewAsync(string filePath)
    {
        _localFilePath = filePath;
        _assetId       = null;
        PathOrUrl      = filePath;
        TitleText      = Path.GetFileNameWithoutExtension(filePath);
        Artist         = "";
        Album          = "";
        Year           = null;
        Bpm            = null;
        TrackId        = "";
        TotalDurationText = "00:00:00";
        StatusText     = "";

        // stop any current PFL but do NOT start new one — user presses ▶ manually
        try { await _api.StopPflAsync(); }
        catch { /* ignore */ }
        IsPlaying = false;

        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        Id3ReadCommand.NotifyCanExecuteChanged();
        NotifyPostImportCommands();

        // auto-read ID3 to populate all fields immediately
        try
        {
            var tags = await _api.ReadId3FromFileAsync(filePath);
            if (tags is not null)
            {
                ApplyId3Peek(tags);
                StatusText = "ID3 tags loaded.";
            }
            else
            {
                StatusText = "No ID3 tags found.";
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Auto ID3 read failed for {Path}", filePath); }
    }

    private void ApplyId3Peek(Id3PeekResponseDto tags)
    {
        if (!string.IsNullOrEmpty(tags.Title))  TitleText = tags.Title;
        if (!string.IsNullOrEmpty(tags.Artist)) Artist    = tags.Artist;
        if (!string.IsNullOrEmpty(tags.Album))  Album     = tags.Album;
        if (tags.Year.HasValue)    Year = (decimal?)tags.Year.Value;
        if (tags.Bpm.HasValue)     Bpm  = tags.Bpm;
        if (tags.DurationMs.HasValue)
            TotalDurationText = TimeSpan.FromMilliseconds(tags.DurationMs.Value).ToString(@"hh\:mm\:ss");

        if (!string.IsNullOrEmpty(tags.Genre))
        {
            var match = GenreOptions.FirstOrDefault(
                g => string.Equals(g, tags.Genre, StringComparison.OrdinalIgnoreCase));
            SelectedGenre = match ?? tags.Genre;
        }

        if (!string.IsNullOrEmpty(tags.PictureBase64))
            SetCoverArtFromBase64(tags.PictureBase64);
    }

    private void SetCoverArtFromBase64(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            _imagePath = null; // embedded — no file path
            CoverArt = new Bitmap(ms);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to decode cover art"); }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync(CancellationToken ct)
    {
        try
        {
            await _api.StopPflAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PFL stop failed"); }
        finally { IsPlaying = false; }
    }

    private bool CanStop() => IsPlaying;

    [RelayCommand(CanExecute = nameof(CanPostImport))]
    private async Task AddToPlaylistAsync(CancellationToken ct)
    {
        if (_assetId is null) return;
        try
        {
            await _api.AddPlaylistItemAsync(
                new AddPlaylistItemRequestDto(_assetId, null, int.MaxValue, "ASSET"), ct);
            StatusText = "Added to playlist.";
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Add to playlist failed"); }
    }

    // ── Import command ────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync(CancellationToken ct)
    {
        IsImporting = true;
        StatusText  = "Starting import…";
        try
        {
            var url = PathOrUrl?.Trim() ?? "";
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                await ImportStreamAsync(url, ct);
                return;
            }

            var req = new ImportRequestDto(
                url,
                SelectedTrackType,
                SelectedCategory?.FormatId,
                SelectedSubcategory?.SubcategoryId,
                ReadRdm:  ReadRdm,
                ReadMmd:  ReadMmd,
                ReadWfrm: ReadWfrm,
                ReadId3:  ReadId3);

            var resp = await _api.StartImportAsync(req, ct);
            if (resp is null) { StatusText = "Import failed: no response from server."; return; }

            StatusText  = "Waiting for import to complete…";
            var assetId = await PollForCompletionAsync(resp.ImportId, ct);
            if (assetId is null) { StatusText = "Import did not complete within the timeout period."; return; }

            _assetId = assetId;
            NotifyPostImportCommands();

            // save form data (from ID3 Read + user edits) to DB — overrides what the pipeline set
            StatusText = "Saving metadata…";
            try
            {
                var imported = await _api.GetAssetDetailAsync(assetId, ct);
                var saveReq = new UpdateAssetRequestDto(
                    string.IsNullOrWhiteSpace(TitleText) ? (imported?.Title ?? "") : TitleText,
                    string.IsNullOrWhiteSpace(Artist)  ? imported?.Artist  : Artist,
                    string.IsNullOrWhiteSpace(Album)   ? imported?.Album   : Album,
                    SelectedCategory?.FormatId         ?? imported?.FormatId,
                    SelectedSubcategory?.SubcategoryId ?? imported?.SubcategoryId,
                    Bpm  ?? imported?.Bpm,
                    (int?)Year ?? imported?.Year,
                    null, null, null, null,
                    string.IsNullOrWhiteSpace(SelectedGenre) ? imported?.Genre : SelectedGenre,
                    string.IsNullOrWhiteSpace(Comments) ? imported?.Comments : Comments,
                    SelectedStatus,
                    StartDateEnabled && DateTime.TryParse(StartDateText, out var sd) ? sd : imported?.StartDate,
                    EndDateEnabled   && DateTime.TryParse(EndDateText,   out var ed) ? ed : imported?.EndDate,
                    null,
                    SelectedTrackType,
                    imported?.CueMarkers,
                    _imagePath,
                    StreamUrl: null,
                    IsVariableDuration: IsVariableDuration);
                await _api.UpdateAssetAsync(assetId, saveReq, ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Post-import metadata save failed"); }

            // Auto-cue runs only when Silence Remover is enabled in settings.
            if (_silenceRemoverEnabled)
            {
                StatusText = "Analyzing cue points and BPM…";
                try
                {
                    await _api.AnalyzeCueAsync([assetId], _cueStartDb, _cueNextStartDb, _cueEndDb, ct);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Auto-cue analysis failed"); }
            }

            // wait for background BPM analysis (typically <2s via BASS_FX_BPM_DecodeGet)
            await Task.Delay(2500, ct);

            // refresh display from DB (duration, BPM, final image path etc.)
            var detail = await _api.GetAssetDetailAsync(assetId, ct);
            if (detail is not null)
            {
                TitleText         = detail.Title;
                Artist            = detail.Artist ?? "";
                Album             = detail.Album  ?? "";
                Year              = (decimal?)detail.Year;
                Bpm               = detail.Bpm;
                TrackId           = assetId;
                TotalDurationText = TimeSpan.FromMilliseconds(detail.DurationMs).ToString(@"hh\:mm\:ss");
                if (!string.IsNullOrEmpty(detail.ImagePath) && File.Exists(detail.ImagePath))
                    SetCoverArtFromFile(detail.ImagePath);
            }
            else
            {
                TrackId = assetId;
            }

            Result     = BuildResult();
            StatusText = "Import completed. Cue analysis queued.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Import cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import failed");
            StatusText = $"Error: {ex.Message}";
        }
        finally { IsImporting = false; }
    }

    private async Task ImportStreamAsync(string streamUrl, CancellationToken ct)
    {
        var name = TitleText?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText = Localizer.Instance?["ti.msg.stream_name_required"] ?? "Enter the stream name in the Title field.";
            return;
        }

        StatusText = Localizer.Instance?["ti.msg.adding_stream"] ?? "Adding stream…";
        var resp = await _api.AddStreamAsync(new AddStreamRequestDto(name, streamUrl), ct);
        if (resp is null)
        {
            StatusText = Localizer.Instance?["ti.msg.stream_add_failed"] ?? "Failed to add the stream.";
            return;
        }

        _assetId = resp.AssetId;
        TrackId  = resp.AssetId;
        NotifyPostImportCommands();

        Result     = BuildResult();
        StatusText = $"Stream dodany: {resp.Name}";
    }

    private bool CanImport() => !string.IsNullOrWhiteSpace(PathOrUrl) && !IsImporting;

    private void NotifyPostImportCommands()
    {
        PlayCommand.NotifyCanExecuteChanged();
        AddToPlaylistCommand.NotifyCanExecuteChanged();
        CueEditorCommand.NotifyCanExecuteChanged();
        Id3ReadCommand.NotifyCanExecuteChanged();
        Id3SaveCommand.NotifyCanExecuteChanged();
    }

    private async Task<string?> PollForCompletionAsync(string importId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            var status = await _api.GetImportStatusAsync(importId, ct);
            if (status?.Status == "COMPLETED") return status.AssetId;
            if (status?.Status == "FAILED")    return null;
        }
        return null;
    }

    // ── ID3 commands ──────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanId3Read))]
    private async Task Id3ReadAsync(CancellationToken ct)
    {
        try
        {
            if (_assetId is not null)
            {
                var detail = await _api.GetAssetDetailAsync(_assetId, ct);
                if (detail is null) return;

                Artist    = detail.Artist ?? "";
                TitleText = detail.Title;
                Album     = detail.Album ?? "";
                Year      = detail.Year;
                Bpm       = detail.Bpm;
                IsVariableDuration = detail.IsVariableDuration;
                TotalDurationText = TimeSpan.FromMilliseconds(detail.DurationMs).ToString(@"hh\:mm\:ss");

                if (detail.StartDate.HasValue)
                {
                    StartDateEnabled = true;
                    StartDateText    = detail.StartDate.Value.ToString("yyyy-MM-dd HH:mm:ss");
                }
                if (detail.EndDate.HasValue)
                {
                    EndDateEnabled = true;
                    EndDateText    = detail.EndDate.Value.ToString("yyyy-MM-dd HH:mm:ss");
                }
                if (!string.IsNullOrEmpty(detail.ImagePath) && File.Exists(detail.ImagePath))
                    SetCoverArtFromFile(detail.ImagePath);

                StatusText = "Metadata reloaded from database.";
            }
            else if (!string.IsNullOrEmpty(_localFilePath))
            {
                var tags = await _api.ReadId3FromFileAsync(_localFilePath, ct);
                if (tags is null) { StatusText = "Could not read ID3 tags from file."; return; }
                ApplyId3Peek(tags);
                StatusText = "ID3 tags loaded from file.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ID3 read failed");
            StatusText = $"ID3 read failed: {ex.Message}";
        }
    }

    private bool CanId3Read() => _assetId is not null || !string.IsNullOrEmpty(_localFilePath);

    [RelayCommand(CanExecute = nameof(CanPostImport))]
    private async Task Id3SaveAsync(CancellationToken ct)
    {
        if (_assetId is null) return;
        try
        {
            var current = await _api.GetAssetDetailAsync(_assetId, ct);

            var req = new UpdateAssetRequestDto(
                TitleText,
                string.IsNullOrWhiteSpace(Artist) ? null : Artist,
                string.IsNullOrWhiteSpace(Album)  ? null : Album,
                SelectedCategory?.FormatId,
                SelectedSubcategory?.SubcategoryId,
                Bpm,
                (int?)Year,
                null, null, null, null,
                string.IsNullOrWhiteSpace(SelectedGenre) ? null : SelectedGenre,
                string.IsNullOrWhiteSpace(Comments) ? null : Comments,
                SelectedStatus,
                StartDateEnabled && DateTime.TryParse(StartDateText, out var sd) ? sd : null,
                EndDateEnabled   && DateTime.TryParse(EndDateText,   out var ed) ? ed : null,
                null,
                SelectedTrackType,
                current?.CueMarkers,
                _imagePath,
                StreamUrl: null,
                IsVariableDuration: IsVariableDuration);

            await _api.UpdateAssetAsync(_assetId, req, ct);
            Result     = BuildResult();
            StatusText = "Metadata saved.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ID3 save failed");
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    private bool CanPostImport() => _assetId is not null;

    // ── CUE Editor command ────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanPostImport))]
    private void CueEditor()
    {
        if (_assetId is null) return;
        _navigation.OpenOrFocusWindow<TrackEditorWindow>(_assetId);
    }

    // ── Image commands ────────────────────────────────────────────────────────
    public void SetCoverArtFromFile(string filePath)
    {
        try
        {
            _imagePath = filePath;
            CoverArt = new Bitmap(filePath);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to load cover art from {Path}", filePath); }
    }

    [RelayCommand]
    private void ClearImage()
    {
        _imagePath = null;
        CoverArt = null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private TrackImportResult BuildResult() => new()
    {
        FilePath               = PathOrUrl.Trim(),
        FormatId               = SelectedCategory?.FormatId,
        SubcategoryId          = SelectedSubcategory?.SubcategoryId,
        AssetType              = SelectedTrackType,
        Status                 = SelectedStatus,
        StartDate              = StartDateEnabled && DateTime.TryParse(StartDateText, out var sd) ? sd : null,
        EndDate                = EndDateEnabled   && DateTime.TryParse(EndDateText,   out var ed) ? ed : null,
        HighPrecisionCuePoints = HighPrecisionCuePoints,
        AssetId                = _assetId
    };
}

public sealed class TrackImportResult
{
    public string    FilePath               { get; init; } = "";
    public string?   FormatId               { get; init; }
    public string?   SubcategoryId          { get; init; }
    public string    AssetType              { get; init; } = "TRACK";
    public string    Status                 { get; init; } = "ACTIVE";
    public DateTime? StartDate              { get; init; }
    public DateTime? EndDate                { get; init; }
    public bool      HighPrecisionCuePoints { get; init; }
    public string?   AssetId               { get; init; }
}
