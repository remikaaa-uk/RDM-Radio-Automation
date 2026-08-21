using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.ViewModels;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace RDM.UI.Views;

public partial class TracksManagerWindow : Window
{
    private bool   _sortingInProgress;
    private string _sortColumn    = "Artist";
    private bool   _sortAscending = true;

    public TracksManagerWindow()
    {
        InitializeComponent();
        var vm = App.Services.GetRequiredService<TracksManagerViewModel>();
        DataContext = vm;
        vm.ScrollToItemRequested += item =>
        {
            TrackList.SelectedItem = item;
            TrackList.ScrollIntoView(item, TrackList.Columns[0]);
        };
        vm.Activate();
        Closed += (_, _) => vm.Dispose();
    }

    private TracksManagerViewModel? ViewModel => DataContext as TracksManagerViewModel;

    // ── Sorting ───────────────────────────────────────────────────────────────

    private void OnDataGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (_sortingInProgress || ViewModel is null) return;
        e.Handled = true;
        _sortingInProgress = true;
        try
        {
            var colPath = e.Column.SortMemberPath ?? "";
            if (_sortColumn == colPath)
                _sortAscending = !_sortAscending;
            else
            {
                _sortColumn    = colPath;
                _sortAscending = true;
            }
            ViewModel.SortBy(colPath, _sortAscending);
        }
        finally
        {
            _sortingInProgress = false;
        }
    }

    // ── Selection sync ────────────────────────────────────────────────────────

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || TrackList is null) return;
        var selected = TrackList.SelectedItems?
            .OfType<TracksManagerItemViewModel>()
            ?? Enumerable.Empty<TracksManagerItemViewModel>();
        ViewModel.UpdateSelection(selected);
    }

    // ── Menu Edit handlers ────────────────────────────────────────────────────

    private void OnSelectAllClicked(object? sender, RoutedEventArgs e)
    {
        TrackList?.SelectAll();
    }

    private void OnDeselectAllClicked(object? sender, RoutedEventArgs e)
    {
        TrackList?.SelectedItems?.Clear();
    }

    // ── Menu Resets ───────────────────────────────────────────────────────────

    private void OnResetFiltersClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.SelectedFormat = null;
        ViewModel.SelectedStatus = "Show All";
        ViewModel.SelectedType   = "Any Track Type";
        ViewModel.SearchText     = "";
    }

    // ── Import file (opens Track Import dialog) ───────────────────────────────

    private async void OnImportFileClicked(object? sender, RoutedEventArgs e)
    {
        await OpenTrackImportDialogAsync();
    }

    private async void OnImportFileMenuClicked(object? sender, RoutedEventArgs e)
    {
        await OpenTrackImportDialogAsync();
    }

    private async System.Threading.Tasks.Task OpenTrackImportDialogAsync()
    {
        var dialog = App.Services.GetRequiredService<TrackImportWindow>();
        await dialog.ShowDialog(this);
        // TracksManager refreshes automatically via WebSocket AssetImported event.
    }

    // ── Import folder (Directory Import dialog) ───────────────────────────────

    private async void OnImportFolderClicked(object? sender, RoutedEventArgs e)
    {
        await ImportFolderWithDialogAsync();
    }

    private async void OnImportFolderMenuClicked(object? sender, RoutedEventArgs e)
    {
        await ImportFolderWithDialogAsync();
    }

    private async System.Threading.Tasks.Task ImportFolderWithDialogAsync()
    {
        if (ViewModel is null) return;

        var api    = App.Services.GetRequiredService<ApiClientService>();
        var dialog = new DirectoryImportWindow(
            ViewModel.Formats,
            api,
            (files, settings, progress) => ViewModel.ImportFilesAsync(files, settings, progress));

        await dialog.ShowDialog(this);
    }

    // ── Update Tracks (scan folder for files new to the library) ──────────────

    private async void OnUpdateTracksClicked(object? sender, RoutedEventArgs e)
    {
        await OpenUpdateTracksDialogAsync();
    }

    private async void OnUpdateTracksMenuClicked(object? sender, RoutedEventArgs e)
    {
        await OpenUpdateTracksDialogAsync();
    }

    private async System.Threading.Tasks.Task OpenUpdateTracksDialogAsync()
    {
        if (ViewModel is null) return;

        var dialog = App.Services.GetRequiredService<UpdateTracksWindow>();

        // Wire the shared import pipeline to THIS live TracksManagerViewModel so the
        // library list refreshes in place (never a fresh DI instance).
        if (dialog.DataContext is UpdateTracksViewModel vm)
            vm.SetImportRunner((files, settings, progress, ct) =>
                ViewModel.ImportFilesAsync(files, settings, progress, ct));

        await dialog.ShowDialog(this);
        // TracksManager list refreshes via ImportFilesAsync → SearchAsync + WebSocket events.
    }

    // ── Categories Manager ────────────────────────────────────────────────────

    private async void OnCategoriesClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = App.Services.GetRequiredService<CategoriesManagerWindow>();
        await dialog.ShowDialog(this);
        if (ViewModel is not null)
            await ViewModel.ReloadFormatsAsync();
    }

    // ── Delete handlers ───────────────────────────────────────────────────────

    private async void OnDeleteFromDbClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.HasSelection) return;
        var dialog = new SimpleConfirmDialog(
            message: string.Format(
                Localizer.Instance?["tm.confirm.db.msg"]
                    ?? "Delete the selected tracks ({0}) from the database?\nFiles will remain on disk.",
                ViewModel.SelectionCount),
            title:        Localizer.Instance?["tm.confirm.db.title"]  ?? "Delete from database",
            confirmLabel: Localizer.Instance?["tm.confirm.db.button"] ?? "Delete from database",
            cancelLabel:  Localizer.Instance?["common.cancel"]        ?? "Cancel");

        if (await dialog.ShowDialog<bool>(this))
            await ViewModel.DeleteSelectedAsync(deleteFile: false);
    }

    private async void OnDeleteFromDiskClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.HasSelection) return;
        var dialog = new SimpleConfirmDialog(
            message: string.Format(
                Localizer.Instance?["tm.confirm.disk.msg"]
                    ?? "Delete the selected tracks ({0}) from the database AND physically from disk?\nThis cannot be undone.",
                ViewModel.SelectionCount),
            title:        Localizer.Instance?["tm.confirm.disk.title"]  ?? "Delete from database and disk",
            confirmLabel: Localizer.Instance?["tm.confirm.disk.button"] ?? "Delete from disk",
            cancelLabel:  Localizer.Instance?["common.cancel"]          ?? "Cancel");

        if (await dialog.ShowDialog<bool>(this))
            await ViewModel.DeleteSelectedAsync(deleteFile: true);
    }

    // ── Close ─────────────────────────────────────────────────────────────────

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
