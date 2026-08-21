using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RDM.UI.ViewModels;
using RDM.UI.Localization;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace RDM.UI.Views;

public partial class TrackImportWindow : Window
{
    public TrackImportWindow()
    {
        InitializeComponent();

        var vm = App.Services.GetRequiredService<TrackImportViewModel>();
        DataContext = vm;

        _ = Task.WhenAll(vm.LoadFormatsAsync(), vm.LoadGenresAsync(), vm.LoadCueLevelsAsync());
    }

    private TrackImportViewModel? ViewModel => DataContext as TrackImportViewModel;

    public TrackImportResult? Result => ViewModel?.Result;

    // ── "+" in player — select file for preview (PFL without import) ─────────

    private async void OnPlayerPlusClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || ViewModel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = Localizer.Instance?["ti.picker.audio"] ?? "Select a track to preview",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Localizer.Instance?["common.ft.audio"] ?? "Audio files")
                {
                    Patterns = ["*.mp3", "*.wav", "*.flac", "*.ogg", "*.aac", "*.m4a", "*.wma"]
                }
            ]
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (path is not null)
                await ViewModel.LoadFileForPreviewAsync(path);
        }
    }

    // ── Date pickers ──────────────────────────────────────────────────────────

    private void OnStartDatePickerClicked(object? sender, RoutedEventArgs e)
        => ShowDatePicker(sender as Button, isStart: true);

    private void OnEndDatePickerClicked(object? sender, RoutedEventArgs e)
        => ShowDatePicker(sender as Button, isStart: false);

    private void ShowDatePicker(Button? anchor, bool isStart)
    {
        if (anchor is null || ViewModel is null) return;

        var currentText = isStart ? ViewModel.StartDateText : ViewModel.EndDateText;

        var calendar = new Calendar { SelectionMode = CalendarSelectionMode.SingleDate };
        if (DateTime.TryParse(currentText, out var current))
            calendar.SelectedDate = current.Date;

        var flyout = new Flyout { Content = calendar };

        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (calendar.SelectedDate is not DateTime date) return;

            var time = DateTime.TryParse(currentText, out var existing)
                ? existing.TimeOfDay
                : DateTime.Now.TimeOfDay;

            var text = date.Date.Add(time).ToString("yyyy-MM-dd HH:mm:ss");
            if (isStart) { ViewModel.StartDateText = text; ViewModel.StartDateEnabled = true; }
            else         { ViewModel.EndDateText   = text; ViewModel.EndDateEnabled   = true; }

            flyout.Hide();
        };

        flyout.ShowAt(anchor);
    }

    // ── Image tab ────────────────────────────────────────────────────────────

    private async void OnSelectImageClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || ViewModel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = Localizer.Instance?["ti.picker.image"] ?? "Select cover art",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Localizer.Instance?["ti.ft.image"] ?? "Image files")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif", "*.webp"]
                }
            ]
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (path is not null)
                ViewModel.SetCoverArtFromFile(path);
        }
    }

    // ── Close ────────────────────────────────────────────────────────────────

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
