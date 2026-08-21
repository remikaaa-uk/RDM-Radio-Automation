using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using RDM.UI.Localization;
using RDM.UI.ViewModels;
using System;
using System.Threading.Tasks;

namespace RDM.UI.Views;

public partial class UpdateTracksWindow : Window
{
    public UpdateTracksWindow()
    {
        InitializeComponent();

        var vm = App.Services.GetRequiredService<UpdateTracksViewModel>();
        DataContext = vm;

        _ = Task.WhenAll(vm.LoadFormatsAsync(), vm.LoadGenresAsync());
    }

    private UpdateTracksViewModel? ViewModel => DataContext as UpdateTracksViewModel;

    // ── Browse ────────────────────────────────────────────────────────────────

    private async void OnBrowseClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || ViewModel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = Localizer.Instance?["ut.picker.folder"] ?? "Select folder to scan",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var path = folders[0].TryGetLocalPath();
            if (path is not null)
                ViewModel.FolderPath = path;
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

    // ── Cancel / close ──────────────────────────────────────────────────────────

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        // While a scan/import is running, Cancel aborts it; otherwise it closes the dialog.
        if (ViewModel?.IsBusy == true)
            ViewModel.RequestCancel();
        else
            Close();
    }
}
