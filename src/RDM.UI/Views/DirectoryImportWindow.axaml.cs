using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using RDM.Core.Constants;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.Views;

public partial class DirectoryImportWindow : Window
{
    private readonly ApiClientService _api;
    private readonly Func<IReadOnlyList<string>, DirectoryImportSettings, IProgress<(int done, int total)>, Task>? _importDelegate;

    private List<AssetFormatDto> _formats;

    // ── Constructors ──────────────────────────────────────────────────────────

    public DirectoryImportWindow() : this(Enumerable.Empty<AssetFormatDto>(), null!, null) { }

    public DirectoryImportWindow(
        IEnumerable<AssetFormatDto> formats,
        ApiClientService api,
        Func<IReadOnlyList<string>, DirectoryImportSettings, IProgress<(int done, int total)>, Task>? importDelegate)
    {
        _api            = api;
        _importDelegate = importDelegate;
        _formats        = formats.ToList();

        InitializeComponent();

        TrackTypeComboBox.ItemsSource   = new[] { "TRACK", "CART", "SWEEPER", "VOICETRACK" };
        TrackTypeComboBox.SelectedIndex = 0;

        AfterPlayComboBox.ItemsSource   = new[] { "Nothing", "Remove" };
        AfterPlayComboBox.SelectedIndex = 0;

        TrackStatusComboBox.ItemsSource   = new[] { "ACTIVE", "DISABLED", "PENDING_REVIEW" };
        TrackStatusComboBox.SelectedIndex = 0;

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        StartDateTextBox.Text = now;
        EndDateTextBox.Text   = now;

        BindFormats();
        CategoryComboBox.SelectionChanged += OnCategorySelectionChanged;
        Opened += OnWindowOpened;
    }

    // ── Initialization ────────────────────────────────────────────────────────

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        await LoadGenresAsync();
    }

    private void BindFormats()
    {
        var selected = CategoryComboBox.SelectedItem as AssetFormatDto;
        CategoryComboBox.ItemsSource = _formats;
        if (selected is not null)
            CategoryComboBox.SelectedItem = _formats.FirstOrDefault(f => f.FormatId == selected.FormatId);
        else if (_formats.Count > 0)
            CategoryComboBox.SelectedIndex = 0;
    }

    private async void OnCategorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var formatId = (CategoryComboBox.SelectedItem as AssetFormatDto)?.FormatId;
        await LoadSubcategoriesAsync(formatId);
    }

    private async Task LoadGenresAsync()
    {
        if (_api is null) return;
        try
        {
            var envelope = await _api.GetGenresAsync();
            var items = envelope?.Items ?? Array.Empty<GenreDto>();
            GenreComboBox.ItemsSource = items;
            GenreComboBox.SelectedIndex = items.Count > 0 ? 0 : -1;
        }
        catch { /* non-fatal */ }
    }

    private async Task LoadSubcategoriesAsync(string? formatId)
    {
        SubcategoryComboBox.ItemsSource = null;
        if (_api is null || formatId is null) return;
        try
        {
            var envelope = await _api.GetSubcategoriesAsync(formatId);
            var items = envelope?.Items ?? Array.Empty<SubcategoryDto>();
            SubcategoryComboBox.ItemsSource = items;
            SubcategoryComboBox.SelectedIndex = items.Count > 0 ? 0 : -1;
        }
        catch { /* non-fatal */ }
    }

    // ── Browse ────────────────────────────────────────────────────────────────

    private async void OnBrowsePathClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = Localizer.Instance?["di.picker.directory"] ?? "Select directory to import",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var path = folders[0].TryGetLocalPath();
            if (path is not null)
                PathTextBox.Text = path;
        }
    }

    // ── Date pickers ──────────────────────────────────────────────────────────

    private void OnStartDatePickerClicked(object? sender, RoutedEventArgs e)
        => ShowDatePicker(sender as Button, StartDateTextBox, StartDateEnabledCheckBox);

    private void OnEndDatePickerClicked(object? sender, RoutedEventArgs e)
        => ShowDatePicker(sender as Button, EndDateTextBox, EndDateEnabledCheckBox);

    private static void ShowDatePicker(Button? anchor, TextBox target, CheckBox enabled)
    {
        if (anchor is null) return;

        var calendar = new Calendar { SelectionMode = CalendarSelectionMode.SingleDate };
        if (DateTime.TryParse(target.Text, out var current))
            calendar.SelectedDate = current.Date;

        var flyout = new Flyout { Content = calendar };

        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (calendar.SelectedDate is not DateTime date) return;

            var time = DateTime.TryParse(target.Text, out var existing)
                ? existing.TimeOfDay
                : DateTime.Now.TimeOfDay;

            target.Text       = date.Date.Add(time).ToString("yyyy-MM-dd HH:mm:ss");
            enabled.IsChecked = true;
            flyout.Hide();
        };

        flyout.ShowAt(anchor);
    }

    // ── Categories button ─────────────────────────────────────────────────────

    private async void OnCategoriesClicked(object? sender, RoutedEventArgs e)
    {
        var window = App.Services.GetRequiredService<CategoriesManagerWindow>();
        await window.ShowDialog(this);

        if (_api is null) return;
        try
        {
            var envelope = await _api.GetFormatsAsync();
            _formats = envelope?.Items?.ToList() ?? _formats;
            BindFormats();
        }
        catch { /* non-fatal */ }
    }

    // ── Import ────────────────────────────────────────────────────────────────

    private async void OnImportDirectoryClicked(object? sender, RoutedEventArgs e)
    {
        var folderPath = PathTextBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(folderPath) || _importDelegate is null)
        {
            Close();
            return;
        }

        var settings = BuildSettings();

        var searchOption = settings.CompleteScan
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        IReadOnlyList<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(folderPath, "*.*", searchOption)
                .Where(SupportedAudioExtensions.IsSupported)
                .ToList();
        }
        catch
        {
            files = Array.Empty<string>();
        }

        if (files.Count == 0)
        {
            Close();
            return;
        }

        ImportDirectoryButton.IsEnabled = false;
        ProgressPanel.IsVisible         = true;
        ProgressStatusText.Text         = $"Importowanie 0 / {files.Count}…";
        ProgressCountText.Text          = $"0 / {files.Count}";
        ImportProgressBar.Value         = 0;

        var progress = new Progress<(int done, int total)>(p =>
        {
            ProgressStatusText.Text = $"Importowanie {p.done} / {p.total}…";
            ProgressCountText.Text  = $"{p.done} / {p.total}";
            ImportProgressBar.Value = p.total > 0 ? (double)p.done / p.total : 0;
        });

        try
        {
            await _importDelegate(files, settings, progress);
        }
        catch { /* errors are logged inside ViewModel */ }

        Close();
    }

    private DirectoryImportSettings BuildSettings()
    {
        DateTime? startDate = null;
        if (StartDateEnabledCheckBox.IsChecked == true &&
            DateTime.TryParse(StartDateTextBox.Text, out var sd))
            startDate = sd;

        DateTime? endDate = null;
        if (EndDateEnabledCheckBox.IsChecked == true &&
            DateTime.TryParse(EndDateTextBox.Text, out var ed))
            endDate = ed;

        int.TryParse(PlayTrackForTextBox.Text, out var playTimes);

        return new DirectoryImportSettings
        {
            FolderPath            = PathTextBox.Text ?? "",
            CompleteScan          = CompleteScanCheckBox.IsChecked == true,
            CategoryFormatId      = (CategoryComboBox.SelectedItem as AssetFormatDto)?.FormatId,
            SubcategoryId         = (SubcategoryComboBox.SelectedItem as SubcategoryDto)?.SubcategoryId,
            AssetType             = TrackTypeComboBox.SelectedItem as string ?? "TRACK",
            ReadRdm               = ReadRdmCheckBox.IsChecked == true,
            ReadMmd               = false,   // .MMD reading disabled
            ReadWfrm              = ReadWfrmCheckBox.IsChecked == true,
            ReadId3               = ReadId3CheckBox.IsChecked == true,
            AutoDetectBpm         = AutoBpmCheckBox.IsChecked == true,
            Bs1770LoudnessAnalyze = Bs1770CheckBox.IsChecked == true,
            StartDate             = startDate,
            EndDate               = endDate,
            PlayTrackForTimes     = playTimes,
            AfterPlay             = AfterPlayComboBox.SelectedItem as string ?? "Nothing",
            AutoGenre             = AutoGenreCheckBox.IsChecked == true,
            SelectedGenreId       = AutoGenreCheckBox.IsChecked != true
                                    ? (GenreComboBox.SelectedItem as GenreDto)?.Name : null,
            TrackStatus           = TrackStatusComboBox.SelectedItem as string ?? "ACTIVE",
        };
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

public sealed class DirectoryImportSettings
{
    public string    FolderPath            { get; set; } = "";
    public bool      CompleteScan          { get; set; } = true;
    public string?   CategoryFormatId      { get; set; }
    public string?   SubcategoryId         { get; set; }
    public string    AssetType             { get; set; } = "TRACK";
    public bool      ReadRdm               { get; set; } = true;
    public bool      ReadMmd               { get; set; } = true;
    public bool      ReadWfrm              { get; set; } = true;
    public bool      ReadId3               { get; set; } = true;
    public bool      AutoDetectBpm         { get; set; } = true;
    public bool      Bs1770LoudnessAnalyze { get; set; }
    public DateTime? StartDate             { get; set; }
    public DateTime? EndDate               { get; set; }
    public int       PlayTrackForTimes     { get; set; }
    public string    AfterPlay             { get; set; } = "Nothing";
    public bool      AutoGenre             { get; set; } = true;
    public string?   SelectedGenreId       { get; set; }
    public string    TrackStatus           { get; set; } = "ACTIVE";
}
