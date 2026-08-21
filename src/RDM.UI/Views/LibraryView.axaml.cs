using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RDM.UI.ViewModels;
using System;

namespace RDM.UI.Views;

public partial class LibraryView : UserControl
{
    private Point  _dragStart;
    private bool   _isDragReady;
    private bool   _sortingInProgress;
    private string _sortColumn    = "Artist";
    private bool   _sortAscending = true;

    public LibraryView()
    {
        InitializeComponent();

        // Tunnel handlers fire before DataGrid captures the pointer for row
        // selection — same pattern as PlaylistView's internal reorder.
        LibraryDataGrid.AddHandler(InputElement.PointerPressedEvent,
            OnDataGridPointerPressed,  RoutingStrategies.Tunnel);
        LibraryDataGrid.AddHandler(InputElement.PointerMovedEvent,
            OnDataGridPointerMoved,    RoutingStrategies.Tunnel);
        LibraryDataGrid.AddHandler(InputElement.PointerReleasedEvent,
            OnDataGridPointerReleased, RoutingStrategies.Tunnel);
    }

    private LibraryViewModel? ViewModel => DataContext as LibraryViewModel;

    // ── Double-tap: add to playlist ───────────────────────────────────────────

    private void OnDataGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel?.AddToPlaylistCommand.CanExecute(null) == true)
            _ = ViewModel.AddToPlaylistCommand.ExecuteAsync(null);
    }

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

    // ── Drag & drop — source ──────────────────────────────────────────────────

    private void OnDataGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragStart   = e.GetPosition(this);
            _isDragReady = true;
        }
    }

    private void OnDataGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragReady = false;
    }

    private async void OnDataGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragReady || ViewModel?.SelectedItem is null) return;

        var pos  = e.GetPosition(this);
        var diff = pos - _dragStart;
        if (Math.Abs(diff.X) < 5 && Math.Abs(diff.Y) < 5) return;

        _isDragReady = false;

        var dragData = new DataObject();
        dragData.Set("rdm-library-asset-id", ViewModel.SelectedItem.AssetId);
        await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Copy);
    }
}
