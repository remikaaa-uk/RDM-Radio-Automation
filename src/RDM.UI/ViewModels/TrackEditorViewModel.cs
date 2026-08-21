using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

public sealed partial class TrackEditorViewModel : ObservableObject, IHasUnsavedChanges, IDisposable
{
    private readonly ApiClientService              _api;
    private readonly AudioSettings                 _audioSettings;
    private readonly ILogger<TrackEditorViewModel> _logger;

    public CueEditorViewModel CueEditor { get; }

    // ── Identity ──────────────────────────────────────────────────────────────

    public string AssetId { get; private set; } = "";

    // ── Navigation ────────────────────────────────────────────────────────────

    private IReadOnlyList<string> _navIds  = [];
    private int                   _navIndex;
    private int                   _navTotal;

    public bool   HasNavigation => _navTotal > 0;
    public bool   HasPrevious   => _navIndex > 0;
    public bool   HasNext       => _navIndex < _navTotal - 1;
    public string NavLabel      => HasNavigation ? $"{_navIndex + 1} / {_navTotal}" : "";

    // ── Settings tab ─────────────────────────────────────────────────────────

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string? _streamUrl;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackLabel))]
    private string _title  = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackLabel))]
    private string _artist = "";
    [ObservableProperty] private int? _bpm;
    [ObservableProperty] private int? _year;
    [ObservableProperty] private byte? _rating;
    [ObservableProperty] private string _status = "ACTIVE";
    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private DateTime? _endDate;
    [ObservableProperty] private uint? _playLimit;

    /// <summary>
    /// "Artist – Title" for the bottom bar, so the edited asset stays identifiable on every tab
    /// (the Cue Editor shows no metadata of its own). Empty until an asset is loaded; falls back to
    /// whichever part exists — sweepers and internet streams often carry no artist.
    /// </summary>
    public string TrackLabel
    {
        get
        {
            var artist = Artist?.Trim() ?? "";
            var title  = Title?.Trim()  ?? "";
            if (artist.Length == 0) return title;
            if (title.Length  == 0) return artist;
            return $"{artist} – {title}";
        }
    }

    // ── Details tab ───────────────────────────────────────────────────────────

    [ObservableProperty] private string? _album;
    [ObservableProperty] private string? _mood;
    [ObservableProperty] private string? _gender;
    [ObservableProperty] private string? _language;
    [ObservableProperty] private string? _comments;
    [ObservableProperty] private decimal? _loudnessLufs;
    [ObservableProperty] private decimal? _loudnessPeak;
    [ObservableProperty] private bool _isDamaged;
    [ObservableProperty] private bool _isVariableDuration;
    [ObservableProperty] private string? _imagePath;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _coverImage;

    // ── Waveform ─────────────────────────────────────────────────────────────

    [ObservableProperty] private float[]? _waveformPeaks;

    // ── UI state ──────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _hasUnsavedChanges;

    bool IHasUnsavedChanges.HasUnsavedChanges => HasUnsavedChanges;

    // ── Formats ───────────────────────────────────────────────────────────────

    public ObservableCollection<AssetFormatDto>  Formats        { get; } = new();
    public ObservableCollection<SubcategoryDto>  Subcategories  { get; } = new();
    public ObservableCollection<string>          Genres         { get; } = new();

    [ObservableProperty] private AssetFormatDto?  _selectedFormat;
    [ObservableProperty] private SubcategoryDto?  _selectedSubcategory;
    [ObservableProperty] private string?          _selectedGenre;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInternetStream))]
    [NotifyPropertyChangedFor(nameof(IsRegularAsset))]
    private string _selectedAssetType = "Track";

    public bool IsInternetStream => SelectedAssetType == "InternetStream";
    public bool IsRegularAsset   => !IsInternetStream;

    private string? _pendingSubcategoryId;

    // ── Static option lists ───────────────────────────────────────────────────

    public IReadOnlyList<string> StatusOptions    { get; } = ["ACTIVE", "DISABLED", "PENDING_REVIEW"];
    public IReadOnlyList<string> AssetTypeOptions { get; } = ["Track", "Sweeper", "InternetStream"];

    public TrackEditorViewModel(
        ApiClientService api,
        AudioSettings audioSettings,
        CueEditorViewModel cueEditor,
        ILogger<TrackEditorViewModel> logger)
    {
        _api           = api;
        _audioSettings = audioSettings;
        _logger        = logger;
        CueEditor      = cueEditor;
        CueEditor.AutoFadeOutDurationS    = audioSettings.AutoFadeOutDurationS;
        CueEditor.SilenceRemoverEnabled   = audioSettings.SilenceRemoverEnabled;
        CueEditor.SilenceStartThresholdDb = (double)audioSettings.SilenceStartThresholdDb;
        CueEditor.SilenceMixThresholdDb   = (double)audioSettings.SilenceMixThresholdDb;
        CueEditor.SilenceEndThresholdDb   = (double)audioSettings.SilenceEndThresholdDb;
        CueEditor.SetSaveDelegate(async markers =>
        {
            var dto = BuildUpdateDto() with { CueMarkers = markers };
            return await _api.UpdateAssetAsync(AssetId, dto);
        });
        _api.WaveformReady += OnWaveformReady;
    }

    // ── Initialization (from NavigationService via IParameterizedWindow) ──────

    public async Task InitializeAsync(string assetId)
    {
        // Runs while AssetId still points at the asset being left: flushes its pending cue edit,
        // stops PFL playback and blanks the waveform. No-op on the first open.
        await CueEditor.ResetForAssetSwitchAsync();
        WaveformPeaks = null;

        AssetId = assetId;
        OnPropertyChanged(nameof(AssetId));
        IsBusy  = true;
        try
        {
            var detail = await _api.GetAssetDetailAsync(assetId);
            if (detail is null) { StatusMessage = Localizer.Instance?["te.msg.asset_not_found"] ?? "Asset not found."; return; }

            await Task.WhenAll(LoadFormatsAsync(), LoadGenresAsync());
            PopulateFromDto(detail);
            OnPropertyChanged(nameof(Status));
            if (detail.AssetType != "InternetStream")
            {
                await LoadWaveformFromApiAsync(assetId);
                CueEditor.Initialize(detail);
                CueEditor.SetWaveformPeaks(WaveformPeaks);
            }
            HasUnsavedChanges = false;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "TrackEditor init failed"); }
        finally { IsBusy = false; }
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    public void SetNavContext(TrackEditorNavContext ctx)
    {
        _navIds   = ctx.AssetIds;
        _navIndex = ctx.CurrentIndex;
        _navTotal = ctx.AssetIds.Count;
        RefreshNavState();
    }

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private async Task PreviousTrackAsync()
    {
        _navIndex--;
        await InitializeAsync(_navIds[_navIndex]);
        RefreshNavState();
    }

    [RelayCommand(CanExecute = nameof(HasNext))]
    private async Task NextTrackAsync()
    {
        _navIndex++;
        await InitializeAsync(_navIds[_navIndex]);
        RefreshNavState();
    }

    private void RefreshNavState()
    {
        OnPropertyChanged(nameof(HasNavigation));
        OnPropertyChanged(nameof(HasPrevious));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(NavLabel));
        PreviousTrackCommand.NotifyCanExecuteChanged();
        NextTrackCommand.NotifyCanExecuteChanged();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        StatusMessage = null;
        try
        {
            var dto = BuildUpdateDto();
            var ok  = await _api.UpdateAssetAsync(AssetId, dto);
            StatusMessage     = ok
                ? Localizer.Instance?["te.msg.saved"]      ?? "Saved."
                : Localizer.Instance?["te.msg.save_error"] ?? "Save error.";
            HasUnsavedChanges = !ok;
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(Localizer.Instance?["te.msg.error"] ?? "Error: {0}", ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AnalyzeLoudnessAsync()
    {
        await _api.AnalyzeLoudnessAsync(new[] { AssetId });
        StatusMessage = Localizer.Instance?["te.msg.loudness_scheduled"] ?? "BS1770 analysis scheduled in the background.";
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void PopulateFromDto(AssetDetailDto d)
    {
        FilePath       = d.RdmFilePath;
        StreamUrl      = d.StreamUrl;
        Title          = d.Title ?? "";
        Artist         = d.Artist ?? "";
        _pendingSubcategoryId = d.SubcategoryId;
        SelectedFormat = Formats.FirstOrDefault(f => f.FormatId == d.FormatId);
        Bpm       = d.Bpm.HasValue ? (int?)Math.Round(d.Bpm.Value) : null;
        Year      = d.Year;
        Rating    = d.Rating;
        Status    = d.Status;
        StartDate = d.StartDate;
        EndDate   = d.EndDate;
        PlayLimit = d.PlayLimit;
        Album     = d.Album;
        Mood      = d.Mood;
        Gender    = d.Gender;
        Language  = d.Language;
        SelectedGenre     = d.Genre;
        SelectedAssetType = d.AssetType is "Track" or "Sweeper" or "InternetStream" ? d.AssetType : "Track";
        Comments          = d.Comments;
        LoudnessLufs = d.LoudnessLufs;
        LoudnessPeak = d.LoudnessPeak;
        IsDamaged    = d.IsDamaged;
        IsVariableDuration = d.IsVariableDuration;
        ImagePath    = d.ImagePath;

        if (!string.IsNullOrEmpty(d.ImagePath) && File.Exists(d.ImagePath))
        {
            try
            {
                using var stream = File.OpenRead(d.ImagePath);
                CoverImage = new Avalonia.Media.Imaging.Bitmap(stream);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load cover image bitmap from {Path}", d.ImagePath);
                CoverImage = null;
            }
        }
        else
        {
            CoverImage = null;
        }
    }

    private UpdateAssetRequestDto BuildUpdateDto() => new(
        Title:         Title ?? "",
        Artist:        string.IsNullOrWhiteSpace(Artist) ? null : Artist,
        Album:         Album,
        FormatId:      string.IsNullOrEmpty(SelectedFormat?.FormatId) ? null : SelectedFormat.FormatId,
        SubcategoryId: SelectedSubcategory?.SubcategoryId,
        Bpm:           (decimal?)Bpm,
        Year:          Year,
        Rating:        Rating,
        Mood:          Mood,
        Gender:        Gender,
        Language:      Language,
        Genre:         string.IsNullOrEmpty(SelectedGenre) ? null : SelectedGenre,
        Comments:      Comments,
        Status:        Status ?? "ACTIVE",
        StartDate:     StartDate,
        EndDate:       EndDate,
        PlayLimit:     PlayLimit,
        AssetType:     SelectedAssetType,
        StreamUrl:     IsInternetStream ? StreamUrl : null,
        IsVariableDuration: IsVariableDuration
    );

    public void Dispose()
    {
        _api.WaveformReady -= OnWaveformReady;
        CueEditor.Dispose();
    }

    private async Task LoadWaveformFromApiAsync(string assetId)
    {
        try
        {
            var compressed = await _api.GetWaveformAsync(assetId);
            WaveformPeaks = WaveformDecoder.Decode(compressed);
            if (WaveformPeaks is null)
                _ = _api.RequestWaveformAsync(assetId);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not load waveform for asset {AssetId}", assetId); }
    }

    private void OnWaveformReady(WaveformReadyPayload p)
    {
        if (p.AssetId != AssetId) return;
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var compressed = await _api.GetWaveformAsync(p.AssetId);
                var peaks = WaveformDecoder.Decode(compressed);
                WaveformPeaks = peaks;
                CueEditor.SetWaveformPeaks(peaks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reload waveform in TrackEditorViewModel");
            }
        });
    }

    partial void OnSelectedFormatChanged(AssetFormatDto? value)
        => _ = LoadSubcategoriesAsync(value?.FormatId);

    private async Task LoadSubcategoriesAsync(string? formatId)
    {
        Subcategories.Clear();
        SelectedSubcategory = null;
        if (string.IsNullOrEmpty(formatId)) return;
        try
        {
            var envelope = await _api.GetSubcategoriesAsync(formatId);
            if (envelope is not null)
                foreach (var s in envelope.Items)
                    Subcategories.Add(s);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Load subcategories failed"); }

        if (_pendingSubcategoryId is not null)
        {
            SelectedSubcategory   = Subcategories.FirstOrDefault(s => s.SubcategoryId == _pendingSubcategoryId);
            _pendingSubcategoryId = null;
        }
    }

    private async Task LoadFormatsAsync()
    {
        var env = await _api.GetFormatsAsync();
        if (env is null) return;
        Formats.Clear();
        Formats.Add(new AssetFormatDto("", "(brak)", null));
        foreach (var f in env.Items) Formats.Add(f);
    }

    private async Task LoadGenresAsync()
    {
        var envelope = await _api.GetGenresAsync();
        if (envelope is null) return;
        Genres.Clear();
        Genres.Add("");
        foreach (var g in envelope.Items) Genres.Add(g.Name);
    }


}
