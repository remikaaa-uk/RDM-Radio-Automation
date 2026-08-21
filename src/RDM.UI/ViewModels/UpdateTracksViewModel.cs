using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Constants;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

/// One row in the Update Tracks results grid — a file found new to the library.
public sealed partial class UpdateTracksItemViewModel : ObservableObject
{
    [ObservableProperty] private bool _isSelected = true;

    public string  FilePath { get; }
    public string  Filename { get; }
    public string? Artist   { get; }
    public string  Title    { get; }
    public string  Duration { get; }
    public string? Folder   { get; }

    public UpdateTracksItemViewModel(NewTrackDto dto)
    {
        FilePath = dto.FilePath;
        Filename = dto.Filename;
        Artist   = dto.Artist;
        Title    = dto.Title ?? Path.GetFileNameWithoutExtension(dto.FilePath);
        Folder   = dto.Folder;
        Duration = dto.DurationMs is > 0
            ? TimeSpan.FromMilliseconds(dto.DurationMs.Value).ToString(@"mm\:ss")
            : "";
    }
}

/// <summary>
/// Update Tracks: scans a folder for files not yet in the library (by path, then
/// by SHA-256 fingerprint) and imports the selected ones through the exact same
/// pipeline as Import Folder (via the injected <see cref="ImportRunner"/>).
/// </summary>
public sealed partial class UpdateTracksViewModel : ObservableObject
{
    private readonly ApiClientService               _api;
    private readonly ILogger<UpdateTracksViewModel> _logger;

    private CancellationTokenSource? _cts;

    /// Runs the shared import pipeline (bound to the live TracksManagerViewModel by
    /// the window that opens this dialog, so the library list refreshes in place).
    public Func<IReadOnlyList<string>, DirectoryImportSettings,
                IProgress<(int done, int total)>, CancellationToken, Task<ImportRunReport>>? ImportRunner { get; set; }

    // ── Folder / scan ─────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string _folderPath = "";

    [ObservableProperty] private bool _includeSubfolders = true;

    // ── Busy / progress ───────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportSelectedCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportSelectedCommand))]
    private bool _isImporting;

    public bool IsBusy => IsScanning || IsImporting;

    [ObservableProperty] private bool   _progressVisible;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _reportText    = "";

    // ── Import settings ───────────────────────────────────────────────────────
    [ObservableProperty] private AssetFormatDto? _selectedCategory;
    [ObservableProperty] private SubcategoryDto? _selectedSubcategory;
    [ObservableProperty] private GenreDto?       _selectedGenre;
    [ObservableProperty] private bool            _autoGenre = true;
    [ObservableProperty] private string          _selectedTrackType = "TRACK";
    [ObservableProperty] private string          _playTrackForText  = "0";
    [ObservableProperty] private string          _selectedAfterPlay = "Nothing";
    [ObservableProperty] private bool            _startDateEnabled;
    [ObservableProperty] private string          _startDateText = "";
    [ObservableProperty] private bool            _endDateEnabled;
    [ObservableProperty] private string          _endDateText = "";
    [ObservableProperty] private bool            _autoDetectBpm = true;
    [ObservableProperty] private string          _selectedStatus = "ACTIVE";
    [ObservableProperty] private bool            _readRdm  = true;
    [ObservableProperty] private bool            _readWfrm = true;
    [ObservableProperty] private bool            _readId3  = true;

    // ── Collections ───────────────────────────────────────────────────────────
    public ObservableCollection<AssetFormatDto>          Formats            { get; } = [];
    public ObservableCollection<SubcategoryDto>          SubcategoryOptions { get; } = [];
    public ObservableCollection<GenreDto>                GenreOptions       { get; } = [];
    public ObservableCollection<UpdateTracksItemViewModel> ScanResults      { get; } = [];

    public IReadOnlyList<string> TrackTypes       { get; } = ["TRACK", "CART", "SWEEPER", "VOICETRACK"];
    public IReadOnlyList<string> AfterPlayOptions { get; } = ["Nothing", "Remove"];
    public IReadOnlyList<string> StatusOptions    { get; } = ["ACTIVE", "DISABLED", "PENDING_REVIEW"];

    // ── Constructor ───────────────────────────────────────────────────────────
    public UpdateTracksViewModel(ApiClientService api, ILogger<UpdateTracksViewModel> logger)
    {
        _api    = api;
        _logger = logger;

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _startDateText = now;
        _endDateText   = now;
    }

    public void SetImportRunner(
        Func<IReadOnlyList<string>, DirectoryImportSettings,
             IProgress<(int done, int total)>, CancellationToken, Task<ImportRunReport>> runner)
    {
        ImportRunner = runner;
        ImportSelectedCommand.NotifyCanExecuteChanged();
    }

    // ── Init ──────────────────────────────────────────────────────────────────
    public async Task LoadFormatsAsync()
    {
        try
        {
            var envelope = await _api.GetFormatsAsync();
            if (envelope is null) return;
            Formats.Clear();
            foreach (var f in envelope.Items) Formats.Add(f);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to load formats in UpdateTracksViewModel"); }
    }

    public async Task LoadGenresAsync()
    {
        try
        {
            var envelope = await _api.GetGenresAsync();
            if (envelope is null) return;
            GenreOptions.Clear();
            foreach (var g in envelope.Items) GenreOptions.Add(g);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to load genres in UpdateTracksViewModel"); }
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
            foreach (var s in envelope.Items) SubcategoryOptions.Add(s);
            if (SubcategoryOptions.Count > 0) SelectedSubcategory = SubcategoryOptions[0];
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to load subcategories"); }
    }

    // ── Scan command ──────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var folder = FolderPath?.Trim() ?? "";
        if (!Directory.Exists(folder))
        {
            StatusMessage = Localizer.Instance?["ut.msg.folder_missing"] ?? "Folder does not exist.";
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsScanning     = true;
        ProgressVisible = true;
        ProgressValue  = 0;
        ReportText     = "";
        ClearResults();
        StatusMessage  = Localizer.Instance?["ut.msg.enumerating"] ?? "Enumerating files…";

        try
        {
            var option = IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = await Task.Run(() =>
                Directory.EnumerateFiles(folder, "*.*", option)
                         .Where(SupportedAudioExtensions.IsSupported)
                         .ToList(), ct);

            if (files.Count == 0)
            {
                StatusMessage = Localizer.Instance?["ut.msg.no_audio"] ?? "No audio files found in the folder.";
                return;
            }

            StatusMessage = string.Format(
                Localizer.Instance?["ut.msg.scanning"] ?? "Scanning {0} / {1}…", 0, files.Count);

            var started = await _api.StartScanAsync(new ScanRequestDto(files), ct);
            if (started is null)
            {
                StatusMessage = Localizer.Instance?["ut.msg.scan_failed"] ?? "Scan failed to start.";
                return;
            }

            ScanStatusDto? status;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(200, ct);
                status = await _api.GetScanStatusAsync(started.ScanId, ct);
                if (status is null) continue;

                ProgressValue = status.Total > 0 ? (double)status.Done / status.Total : 0;
                StatusMessage = string.Format(
                    Localizer.Instance?["ut.msg.scanning"] ?? "Scanning {0} / {1}…", status.Done, status.Total);

                if (status.Status is "COMPLETED" or "FAILED") break;
            }

            if (status.Status == "FAILED")
            {
                StatusMessage = Localizer.Instance?["ut.msg.scan_failed"] ?? "Scan failed.";
                return;
            }

            var results = await _api.GetScanResultsAsync(started.ScanId, ct);
            if (results is null)
            {
                StatusMessage = Localizer.Instance?["ut.msg.results_failed"] ?? "Could not load scan results.";
                return;
            }

            foreach (var r in results)
            {
                var item = new UpdateTracksItemViewModel(r);
                item.PropertyChanged += OnItemPropertyChanged;
                ScanResults.Add(item);
            }

            StatusMessage = results.Count == 0
                ? Localizer.Instance?["ut.msg.up_to_date"] ?? "No new files — the library is up to date."
                : string.Format(Localizer.Instance?["ut.msg.found"] ?? "{0} new file(s) found.", results.Count);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Localizer.Instance?["ut.msg.scan_cancelled"] ?? "Scan cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update Tracks scan failed");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning     = false;
            ProgressVisible = false;
            ImportSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanScan() => !IsBusy && !string.IsNullOrWhiteSpace(FolderPath);

    // ── Selection commands ────────────────────────────────────────────────────
    [RelayCommand] private void SelectAll()   { foreach (var i in ScanResults) i.IsSelected = true; }
    [RelayCommand] private void UnselectAll() { foreach (var i in ScanResults) i.IsSelected = false; }
    [RelayCommand] private void InvertSelection() { foreach (var i in ScanResults) i.IsSelected = !i.IsSelected; }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateTracksItemViewModel.IsSelected))
            ImportSelectedCommand.NotifyCanExecuteChanged();
    }

    // ── Import command ────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportSelectedAsync()
    {
        if (ImportRunner is null) return;

        var selected = ScanResults.Where(i => i.IsSelected).ToList();
        var paths = selected.Select(i => i.FilePath).ToList();
        if (paths.Count == 0) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsImporting    = true;
        ProgressVisible = true;
        ProgressValue  = 0;
        ReportText     = "";

        var progress = new Progress<(int done, int total)>(p =>
        {
            ProgressValue = p.total > 0 ? (double)p.done / p.total : 0;
            StatusMessage = string.Format(
                Localizer.Instance?["ut.msg.importing"] ?? "Importing {0} / {1}…", p.done, p.total);
        });

        try
        {
            var settings = BuildSettings();
            var report   = await ImportRunner(paths, settings, progress, ct);

            ReportText = string.Format(
                Localizer.Instance?["ut.msg.report"] ?? "Imported: {0}   Skipped: {1}   Errors: {2}",
                report.Imported, report.Skipped, report.Errors);
            StatusMessage = Localizer.Instance?["ut.msg.import_done"] ?? "Import complete.";

            // Drop the processed rows so the grid reflects what is left to do.
            foreach (var item in selected)
            {
                item.PropertyChanged -= OnItemPropertyChanged;
                ScanResults.Remove(item);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Localizer.Instance?["ut.msg.import_cancelled"] ?? "Import cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update Tracks import failed");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsImporting    = false;
            ProgressVisible = false;
            ScanCommand.NotifyCanExecuteChanged();
            ImportSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanImport() =>
        !IsBusy && ImportRunner is not null && ScanResults.Any(i => i.IsSelected);

    // ── Cancel ────────────────────────────────────────────────────────────────
    public void RequestCancel() => _cts?.Cancel();

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void ClearResults()
    {
        foreach (var i in ScanResults) i.PropertyChanged -= OnItemPropertyChanged;
        ScanResults.Clear();
    }

    private DirectoryImportSettings BuildSettings()
    {
        DateTime? startDate = StartDateEnabled && DateTime.TryParse(StartDateText, out var sd) ? sd : null;
        DateTime? endDate   = EndDateEnabled   && DateTime.TryParse(EndDateText,   out var ed) ? ed : null;
        int.TryParse(PlayTrackForText, out var playTimes);

        return new DirectoryImportSettings
        {
            FolderPath            = FolderPath ?? "",
            CompleteScan          = IncludeSubfolders,
            CategoryFormatId      = SelectedCategory?.FormatId,
            SubcategoryId         = SelectedSubcategory?.SubcategoryId,
            AssetType             = SelectedTrackType,
            ReadRdm               = ReadRdm,
            ReadMmd               = false,   // .MMD reading disabled
            ReadWfrm              = ReadWfrm,
            ReadId3               = ReadId3,
            AutoDetectBpm         = AutoDetectBpm,
            Bs1770LoudnessAnalyze = false,
            StartDate             = startDate,
            EndDate               = endDate,
            PlayTrackForTimes     = playTimes,
            AfterPlay             = SelectedAfterPlay,
            AutoGenre             = AutoGenre,
            SelectedGenreId       = !AutoGenre ? SelectedGenre?.Name : null,
            TrackStatus           = SelectedStatus,
        };
    }
}
