using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using RDM.UI.Controls;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.ViewModels;
using System;
using System.Linq;

namespace RDM.UI.Views;

public partial class PlaylistBuilderWindow : Window
{
    // ── Library sort state ────────────────────────────────────────────────────
    private bool   _librarySortingInProgress;
    private string _librarySortColumn    = "Artist";
    private bool   _librarySortAscending = true;

    // ── Builder drag state ────────────────────────────────────────────────────
    private int    _dragSourceIndex = -1;
    private Point? _dragStartPoint;
    private bool   _isDragging;
    private int    _dropInsertIndex = -1;

    // Scrolls the list when a drag reaches its top/bottom edge — see DragAutoScroller.
    private DragAutoScroller? _autoScroller;

    public PlaylistBuilderWindow()
    {
        InitializeComponent();
        var vm = App.Services.GetRequiredService<PlaylistBuilderViewModel>();
        DataContext = vm;
        vm.Activate();
        Closed += (_, _) => vm.Dispose();

        SetupBuilderDragDrop();
    }

    private PlaylistBuilderViewModel? ViewModel => DataContext as PlaylistBuilderViewModel;

    // ── Selection sync ──────────────────────────────────────────────────────────

    private void OnBuilderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || BuilderList is null) return;
        var selected = BuilderList.SelectedItems?.OfType<BuilderItemViewModel>()
                       ?? Enumerable.Empty<BuilderItemViewModel>();
        ViewModel.UpdateSelection(selected);
    }

    // ── Library ───────────────────────────────────────────────────────────────

    private void OnLibraryItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel?.AddSelectedToBuilderCommand.CanExecute(null) == true)
            ViewModel.AddSelectedToBuilderCommand.Execute(null);
    }

    private void OnLibraryGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (_librarySortingInProgress || ViewModel is null) return;
        e.Handled = true;
        _librarySortingInProgress = true;
        try
        {
            var col = e.Column.SortMemberPath ?? "";
            if (_librarySortColumn == col)
                _librarySortAscending = !_librarySortAscending;
            else { _librarySortColumn = col; _librarySortAscending = true; }
            ViewModel.SortLibrary(col, _librarySortAscending);
        }
        finally { _librarySortingInProgress = false; }
    }

    // ── Menu ──────────────────────────────────────────────────────────────────

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private async void OnMenuSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var dialog = new SavePlaylistDialog(ViewModel.PlaylistName, ViewModel.LoadedPlaylistId);
        var result = await dialog.ShowDialog<SavePlaylistOptions?>(this);
        if (result is null) return;

        if (result.ToDatabase)
            await ViewModel.SaveToDatabaseAsync(result.Name, result.OverwriteId);
        else
            await ViewModel.SaveToFileAsync(result.Name, result.FilePath!);
    }

    private async void OnMenuOpenClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var api    = App.Services.GetRequiredService<ApiClientService>();
        var dialog = new PlaylistBrowseDialog(api,
            Localizer.Instance?["pbd.open_title"] ?? "Open playlist",
            Localizer.Instance?["pbd.open_action"] ?? "Open",
            showBrowseFile: true);
        var result = await dialog.ShowDialog<BrowsePlaylistResult?>(this);
        if (result is null) return;

        if (result.IsFile)
            await ViewModel.LoadFromFileAsync(result.FilePath!);
        else
            await ViewModel.LoadFromDatabaseAsync(result.PlaylistId!);
    }

    private async void OnMenuDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var api    = App.Services.GetRequiredService<ApiClientService>();
        var dialog = new PlaylistBrowseDialog(api,
            Localizer.Instance?["pbd.delete_title"] ?? "Delete playlist",
            Localizer.Instance?["common.delete"] ?? "Delete",
            showBrowseFile: false);
        var result = await dialog.ShowDialog<BrowsePlaylistResult?>(this);
        if (result is null || result.IsFile) return;

        await ViewModel.DeleteFromDatabaseAsync(result.PlaylistId!);
    }

    // ── Builder drag & drop ───────────────────────────────────────────────────

    private void SetupBuilderDragDrop()
    {
        BuilderList.AddHandler(InputElement.PointerPressedEvent,
            OnBuilderPointerPressed,  RoutingStrategies.Tunnel);
        BuilderList.AddHandler(InputElement.PointerMovedEvent,
            OnBuilderPointerMoved,    RoutingStrategies.Tunnel);
        BuilderList.AddHandler(InputElement.PointerReleasedEvent,
            OnBuilderPointerReleased, RoutingStrategies.Tunnel);
        BuilderList.AddHandler(InputElement.PointerCaptureLostEvent,
            (object? _, PointerCaptureLostEventArgs _2) => ResetDragState(),
            RoutingStrategies.Bubble);
    }

    private void OnBuilderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(BuilderList).Properties.IsLeftButtonPressed) return;

        // Don't start a reorder when the press lands inside the scrollbar (or a row button).
        // The scrollbar is a child of the list and we listen in the tunnel phase — before it —
        // so otherwise the press reads as a grab on the row underneath and the first move steals
        // the Thumb's pointer capture, dropping the scroll mid-drag. The Thumb is not a Button,
        // so testing the whole ScrollBar subtree is what actually covers it.
        var node = e.Source as Visual;
        while (node is not null && node != BuilderList)
        {
            if (node is Button or ScrollBar) return;
            node = node.GetVisualParent();
        }

        var pos = e.GetPosition(BuilderList);
        _dragSourceIndex = GetBuilderItemIndexAt(pos);
        if (_dragSourceIndex >= 0)
            _dragStartPoint = pos;
    }

    private void OnBuilderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSourceIndex < 0 || _dragStartPoint == null) return;
        var pos = e.GetPosition(BuilderList);

        if (!_isDragging)
        {
            var delta = pos - _dragStartPoint.Value;
            if (Math.Abs(delta.Y) < 6) return;
            _isDragging = true;
            e.Pointer.Capture(BuilderList);
        }

        e.Handled = true;
        AutoScroller.Track(pos);
        UpdateDropIndicator(e.GetPosition(DropCanvas));
    }

    private DragAutoScroller AutoScroller => _autoScroller ??= new DragAutoScroller(
        BuilderList,
        listPos =>
        {
            // Content moved under a stationary pointer — recompute against the rows now realized.
            if (BuilderList.TranslatePoint(listPos, DropCanvas) is { } canvasPos)
                UpdateDropIndicator(canvasPos);
        });

    private void OnBuilderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging && _dropInsertIndex >= 0 && ViewModel is not null)
        {
            var src  = _dragSourceIndex;
            var dest = _dropInsertIndex > src ? _dropInsertIndex - 1 : _dropInsertIndex;
            if (src >= 0 && src != dest)
                ViewModel.MoveBuilderItem(src, dest);
        }
        e.Pointer.Capture(null);
        ResetDragState();
    }

    private int GetBuilderItemIndexAt(Point listBoxPos)
    {
        var count = ViewModel?.BuilderItems.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            if (BuilderList.ContainerFromIndex(i) is not Control c) continue;
            var tl = c.TranslatePoint(new Point(0, 0), BuilderList);
            if (tl is null) continue;
            if (listBoxPos.Y >= tl.Value.Y && listBoxPos.Y < tl.Value.Y + c.Bounds.Height)
                return i;
        }
        return -1;
    }

    private void UpdateDropIndicator(Point canvasPos)
    {
        var count = ViewModel?.BuilderItems.Count ?? 0;
        if (count == 0) { HideDropIndicator(); return; }

        _dropInsertIndex = count;
        double indicatorY = double.NaN;

        for (int i = 0; i < count; i++)
        {
            if (BuilderList.ContainerFromIndex(i) is not Control c) continue;
            var tl = c.TranslatePoint(new Point(0, 0), DropCanvas);
            if (tl is null) continue;

            var midY = tl.Value.Y + c.Bounds.Height / 2.0;
            if (canvasPos.Y < midY)
            {
                _dropInsertIndex = i;
                indicatorY = tl.Value.Y;
                break;
            }
            indicatorY = tl.Value.Y + c.Bounds.Height;
        }

        if (double.IsNaN(indicatorY)) { HideDropIndicator(); return; }

        Canvas.SetTop(DropLine, Math.Max(0, indicatorY - 1));
        DropLine.Width     = Math.Max(0, DropCanvas.Bounds.Width);
        DropLine.IsVisible = true;
    }

    private void HideDropIndicator()
    {
        DropLine.IsVisible = false;
        _dropInsertIndex   = -1;
    }

    private void ResetDragState()
    {
        _autoScroller?.Stop();
        HideDropIndicator();
        _dragSourceIndex = -1;
        _dragStartPoint  = null;
        _isDragging      = false;
    }
}
