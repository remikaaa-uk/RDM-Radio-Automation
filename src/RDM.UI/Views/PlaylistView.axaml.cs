using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RDM.UI.Controls;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.Views;

public partial class PlaylistView : UserControl
{
    // ── Reorder drag state (same approach as PlaylistBuilderWindow) ───────────
    private int    _dragSourceIndex = -1;
    private Point? _dragStartPoint;
    private bool   _isDragging;
    private int    _dropInsertIndex = -1;
    private const double DragThreshold = 6.0;

    // Scrolls the list when a drag reaches its top/bottom edge — see DragAutoScroller.
    private DragAutoScroller? _autoScroller;

    private readonly ILogger? _log =
        App.Services?.GetService<ILoggerFactory>()?.CreateLogger("PlaylistView.Drag");

    public PlaylistView()
    {
        InitializeComponent();

        // Tunnel handlers on the list itself fire before the ListBox's own
        // selection logic, and pointer capture keeps moves flowing to us — this
        // is the proven grab-and-drag pattern used by PlaylistBuilderWindow.
        PlaylistListBox.AddHandler(InputElement.PointerPressedEvent,
            OnReorderPointerPressed,  RoutingStrategies.Tunnel);
        PlaylistListBox.AddHandler(InputElement.PointerMovedEvent,
            OnReorderPointerMoved,    RoutingStrategies.Tunnel);
        PlaylistListBox.AddHandler(InputElement.PointerReleasedEvent,
            OnReorderPointerReleased, RoutingStrategies.Tunnel);
        // Only react when the LIST itself loses capture (a genuine cancel, e.g. alt-tab).
        // When we grab capture for a drag, the row container loses its own capture and
        // that event bubbles up here too — ignoring it prevents the drag from being
        // reset the instant it starts.
        PlaylistListBox.AddHandler(InputElement.PointerCaptureLostEvent,
            (object? _, PointerCaptureLostEventArgs ev) =>
            {
                if (ReferenceEquals(ev.Source, PlaylistListBox))
                    ResetDragState();
            },
            RoutingStrategies.Bubble);

        LoadPlaylistButton.Click  += OnLoadPlaylistClicked;
        SavePlaylistButton.Click  += OnSavePlaylistMenuClicked;
        ClearPlaylistButton.Click += OnClearPlaylistClicked;

        this.AddHandler(InputElement.KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel?.IsCursorActive == true)
        {
            ViewModel.ClearCursorCommand.Execute(null);
            e.Handled = true;
        }
    }

    private PlaylistViewModel? ViewModel => DataContext as PlaylistViewModel;

    // ── Internal reorder (grab a row, drag to a new position) ─────────────────

    private void OnReorderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PlaylistListBox).Properties.IsLeftButtonPressed) return;

        // Don't start a drag when the press lands on an action button (PFL/Info/Remove), or
        // anywhere inside the scrollbar. The scrollbar lives inside the ListBox and we listen in
        // the tunnel phase — i.e. before it — so without this the press is read as a grab on the
        // row underneath, and the first move steals the Thumb's pointer capture. The thumb then
        // "lets go" mid-scroll and the drag turns into a track reorder.
        // Testing the whole ScrollBar subtree, not just Button: the Thumb is not a Button
        // (RepeatButton is, which is why the arrows never showed this).
        var node = e.Source as Visual;
        while (node is not null && node != PlaylistListBox)
        {
            if (node is Button or ScrollBar) return;
            node = node.GetVisualParent();
        }

        var pos = e.GetPosition(PlaylistListBox);
        _dragSourceIndex = GetItemIndexAt(pos);
        if (_dragSourceIndex >= 0)
            _dragStartPoint = pos;
        _log?.LogInformation("Pressed: source={Idx} src={Src}", _dragSourceIndex, e.Source?.GetType().Name);
    }

    private void OnReorderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSourceIndex < 0 || _dragStartPoint is null) return;
        var pos = e.GetPosition(PlaylistListBox);

        if (!_isDragging)
        {
            if (Math.Abs(pos.Y - _dragStartPoint.Value.Y) < DragThreshold) return;
            _isDragging = true;
            e.Pointer.Capture(PlaylistListBox);
            _log?.LogInformation("Drag started from index {Idx}", _dragSourceIndex);
        }

        e.Handled = true;
        TrackDragPointer(pos);
        UpdateDropIndicator(e.GetPosition(DropCanvas));
    }

    private async void OnReorderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Not a drag (e.g. a plain click on a row button): leave the event and any
        // Button's pointer capture untouched, or the Click won't fire. This handler
        // runs in the tunnel phase, before the Button processes its own release.
        if (!_isDragging)
        {
            _log?.LogInformation("Released without drag (src={Src})", _dragSourceIndex);
            ResetDragState();
            return;
        }

        // Snapshot the drop index BEFORE ResetDragState() wipes it to -1.
        var src  = _dragSourceIndex;
        var drop = _dropInsertIndex;
        var dest = drop > src ? drop - 1 : drop;
        var item = (src >= 0 && ViewModel is not null && src < ViewModel.Items.Count)
            ? ViewModel.Items[src]
            : null;

        e.Pointer.Capture(null);
        ResetDragState();

        _log?.LogInformation("Released: src={Src} dropInsert={Drop} dest={Dest} item={Item}",
            src, drop, dest, item?.ItemId ?? "(null)");

        if (item is not null && drop >= 0 && src != dest && ViewModel is not null)
        {
            _log?.LogInformation("Calling ReorderItemAsync({Item}, {Dest})", item.ItemId, dest);
            await ViewModel.ReorderItemAsync(item.ItemId, dest);
        }
    }

    // ── External Drag & Drop (library, Windows Explorer) ─────────────────────

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;

        if (e.Data.Contains("rdm-library-asset-id"))
            e.DragEffects = DragDropEffects.Copy;
        else if (e.Data.GetFiles()?.Any(f => IsAudioFile(f.Name)) == true)
            e.DragEffects = DragDropEffects.Copy;

        if (e.DragEffects != DragDropEffects.None)
        {
            TrackDragPointer(e.GetPosition(PlaylistListBox));
            UpdateDropIndicator(e.GetPosition(DropCanvas));
        }
        else
        {
            StopAutoScroll();
        }

        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var dropIndex = _dropInsertIndex;
        StopAutoScroll();
        HideDropIndicator();
        if (ViewModel is null) return;

        if (e.Data.Contains("rdm-library-asset-id")
            && e.Data.Get("rdm-library-asset-id") is string assetId)
        {
            if (dropIndex >= 0)
                await ViewModel.AddAssetAsync(assetId, dropIndex);
            e.Handled = true;
            return;
        }

        var files = e.Data.GetFiles()?.Where(f => IsAudioFile(f.Name)).ToList();
        if (files is { Count: > 0 })
        {
            foreach (var file in files)
                await ViewModel.DropFileCommand.ExecuteAsync(file.Path.LocalPath);
            e.Handled = true;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        StopAutoScroll();
        HideDropIndicator();
    }

    // ── Drop indicator + hit-testing (ContainerFromIndex — virtualization-safe) ─

    // Source row under a position in PlaylistListBox coordinates.
    private int GetItemIndexAt(Point listPos)
    {
        var count = ViewModel?.Items.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            if (PlaylistListBox.ContainerFromIndex(i) is not Control c) continue;
            var tl = c.TranslatePoint(new Point(0, 0), PlaylistListBox);
            if (tl is null) continue;
            if (listPos.Y >= tl.Value.Y && listPos.Y < tl.Value.Y + c.Bounds.Height)
                return i;
        }
        return -1;
    }

    // Sets _dropInsertIndex (0..count) for a position in DropCanvas coordinates
    // and draws the blue insertion line.
    private void UpdateDropIndicator(Point canvasPos)
    {
        var count = ViewModel?.Items.Count ?? 0;
        if (count == 0) { HideDropIndicator(); return; }

        _dropInsertIndex  = count;
        double indicatorY = double.NaN;

        for (int i = 0; i < count; i++)
        {
            if (PlaylistListBox.ContainerFromIndex(i) is not Control c) continue;
            var tl = c.TranslatePoint(new Point(0, 0), DropCanvas);
            if (tl is null) continue;

            var midY = tl.Value.Y + c.Bounds.Height / 2.0;
            if (canvasPos.Y < midY)
            {
                _dropInsertIndex = i;
                indicatorY       = tl.Value.Y;
                break;
            }
            indicatorY = tl.Value.Y + c.Bounds.Height;
        }

        if (double.IsNaN(indicatorY)) { HideDropIndicator(); return; }

        Canvas.SetTop(InsertionIndicator, Math.Max(0, indicatorY - 1));
        InsertionIndicator.Width     = Math.Max(0, DropCanvas.Bounds.Width);
        InsertionIndicator.IsVisible = true;
    }

    private void HideDropIndicator()
    {
        InsertionIndicator.IsVisible = false;
        _dropInsertIndex             = -1;
    }

    private void ResetDragState()
    {
        StopAutoScroll();
        HideDropIndicator();
        _dragSourceIndex = -1;
        _dragStartPoint  = null;
        _isDragging      = false;
    }

    // ── Edge auto-scroll ──────────────────────────────────────────────────────

    private DragAutoScroller AutoScroller => _autoScroller ??= new DragAutoScroller(
        PlaylistListBox,
        listPos =>
        {
            // Content moved under a stationary pointer — recompute against the rows now realized.
            if (PlaylistListBox.TranslatePoint(listPos, DropCanvas) is { } canvasPos)
                UpdateDropIndicator(canvasPos);
        });

    private void TrackDragPointer(Point listPos) => AutoScroller.Track(listPos);

    private void StopAutoScroll() => _autoScroller?.Stop();

    private static bool IsAudioFile(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".mp3" or ".wav" or ".flac" or ".ogg" or ".aac" or ".m4a" or ".wma";
    }

    // ── Clear playlist ────────────────────────────────────────────────────────

    private async void OnClearPlaylistClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new SimpleConfirmDialog(
            message:      Localizer.Instance?["playlist.clear_confirm.question"] ?? "Remove all tracks from the queue?",
            title:        Localizer.Instance?["playlist.clear_confirm.title"]    ?? "Clear queue",
            confirmLabel: Localizer.Instance?["common.clear"]                    ?? "Clear",
            cancelLabel:  Localizer.Instance?["common.cancel"]                   ?? "Cancel");

        if (await dialog.ShowDialog<bool>(owner))
            await ViewModel.ClearPlaylistCommand.ExecuteAsync(null);
    }

    // ── Load playlist ─────────────────────────────────────────────────────────

    private async void OnLoadPlaylistClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Button btn) return;

        var menu = new ContextMenu();

        var envelope = await ViewModel.GetSavedPlaylistsAsync();
        if (envelope is null || envelope.Items.Count == 0)
        {
            // No saved playlists — still expose the M3U option below; just note the empty list.
            menu.Items.Add(new MenuItem { Header = Localizer.Instance?["playlist.load_menu.empty"] ?? "(No saved playlists)", IsEnabled = false });
        }
        else
        {
            var itemSuffix = Localizer.Instance?["playlist.load_menu.item_suffix"] ?? "tr.";
            foreach (var p in envelope.Items.OrderBy(p => p.Name))
            {
                var item = new MenuItem
                {
                    Header = $"{p.Name}   ({p.ItemCount} {itemSuffix} · {p.CreatedAt.ToLocalTime():dd.MM.yyyy})"
                };
                var pid = p.PlaylistId;
                item.Click += async (_, _) =>
                {
                    if (ViewModel is null) return;
                    bool? appendToEnd = await ShowLoadPositionDialogAsync();
                    if (appendToEnd is null) return;
                    await ViewModel.LoadSavedPlaylistAsync(pid, appendToEnd.Value);
                };
                menu.Items.Add(item);
            }
        }

        // ── Load from M3U file ────────────────────────────────────────────────
        menu.Items.Add(new Separator());
        var fromFile = new MenuItem { Header = Localizer.Instance?["playlist.load_from_file"] ?? "Load from M3U file…" };
        fromFile.Click += async (_, _) =>
        {
            if (ViewModel is null) return;
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var opts = new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title          = Localizer.Instance?["playlist.open_m3u_title"] ?? "Open M3U file",
                AllowMultiple  = false,
                FileTypeFilter = new List<Avalonia.Platform.Storage.FilePickerFileType>
                {
                    new Avalonia.Platform.Storage.FilePickerFileType(Localizer.Instance?["playlist.ft_m3u"] ?? "M3U playlist (*.m3u)")
                        { Patterns = new[] { "*.m3u", "*.m3u8" } }
                }
            };
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(opts);
            if (files.Count == 0) return;

            bool? appendToEnd = await ShowLoadPositionDialogAsync();
            if (appendToEnd is null) return;
            await ViewModel.LoadFromM3uAsync(files[0].Path.LocalPath, appendToEnd.Value);
        };
        menu.Items.Add(fromFile);

        menu.Open(btn);
    }

    private Task<bool?> ShowLoadPositionDialogAsync()
    {
        var tcs      = new TaskCompletionSource<bool?>();
        var endBtn   = new Button { Content = Localizer.Instance?["playlist.load_pos.end"]   ?? "At the end of the queue",   Padding = new Thickness(12, 6) };
        var startBtn = new Button { Content = Localizer.Instance?["playlist.load_pos.start"] ?? "At the start of the queue", Padding = new Thickness(12, 6) };
        var canBtn   = new Button { Content = Localizer.Instance?["common.cancel"] ?? "Cancel",                              Padding = new Thickness(12, 6) };

        var dialog = new Window
        {
            Title                 = Localizer.Instance?["playlist.load_pos.title"] ?? "Where to load?",
            Width                 = 340,
            Height                = 130,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new StackPanel
            {
                Margin   = new Thickness(16),
                Spacing  = 12,
                Children =
                {
                    new TextBlock { Text = Localizer.Instance?["playlist.load_pos.question"] ?? "Where should the loaded playlist be inserted?" },
                    new StackPanel
                    {
                        Orientation         = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing             = 6,
                        Children            = { canBtn, startBtn, endBtn }
                    }
                }
            }
        };

        endBtn.Click   += (_, _) => { tcs.TrySetResult(true);  dialog.Close(); };
        startBtn.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        canBtn.Click   += (_, _) => { tcs.TrySetResult(null);  dialog.Close(); };
        dialog.Closed  += (_, _) => tcs.TrySetResult(null);

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is not null) _ = dialog.ShowDialog(owner);
        else                       dialog.Show();

        return tcs.Task;
    }

    // ── Save playlist ─────────────────────────────────────────────────────────

    private void OnSavePlaylistMenuClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        var saveDb  = new MenuItem { Header = Localizer.Instance?["playlist.save_to_db"] ?? "Save to database" };
        var saveM3u = new MenuItem { Header = Localizer.Instance?["playlist.export_m3u"] ?? "Export as M3U…" };

        saveDb.Click  += async (_, _) => await SaveToDatabaseAsync();
        saveM3u.Click += async (_, _) => await ExportM3uAsync();

        var menu = new ContextMenu { Items = { saveDb, saveM3u } };
        menu.Open(SavePlaylistButton);
    }

    /// Saves the queue as a new playlist or overwrites an existing one — the target is
    /// picked in SavePlaylistDialog, which defaults to the playlist the queue came from.
    private async Task SaveToDatabaseAsync()
    {
        if (ViewModel is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var summary  = await ViewModel.GetSaveSummaryAsync();
        var envelope = await ViewModel.GetSavedPlaylistsAsync();

        var dialog = new SavePlaylistDialog(
            initialName:        ViewModel.LoadedSavedPlaylistName ?? string.Empty,
            loadedPlaylistId:   ViewModel.LoadedSavedPlaylistId,
            loadedPlaylistName: ViewModel.LoadedSavedPlaylistName,
            existingPlaylists:  envelope?.Items,
            databaseOnly:       true,                  // M3U export has its own menu entry
            externalItemCount:  summary.ExternalCount);

        var result = await dialog.ShowDialog<SavePlaylistOptions?>(owner);
        if (result is null) return;

        if (result.OverwriteId is not null)
        {
            var question = Localizer.Instance?.Format(
                               "playlist.overwrite_confirm.question", result.Name, summary.SavableCount)
                           ?? $"Replace the contents of \"{result.Name}\" with the current queue ({summary.SavableCount} tracks)?";

            var confirm = new SimpleConfirmDialog(
                message:      question,
                title:        Localizer.Instance?["playlist.overwrite_confirm.title"] ?? "Overwrite playlist",
                confirmLabel: Localizer.Instance?["common.overwrite"]                 ?? "Overwrite",
                cancelLabel:  Localizer.Instance?["common.cancel"]                    ?? "Cancel");

            if (!await confirm.ShowDialog<bool>(owner)) return;
        }

        await ViewModel.SaveCurrentPlaylistAsync(result.Name, result.OverwriteId);
    }

    private async Task ExportM3uAsync()
    {
        if (ViewModel is null) return;

        var opts = new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title             = Localizer.Instance?["playlist.export_title"] ?? "Export playlist as M3U",
            SuggestedFileName = Localizer.Instance?["playlist.export_default_name"] ?? "playlist",
            FileTypeChoices   = new List<Avalonia.Platform.Storage.FilePickerFileType>
            {
                new Avalonia.Platform.Storage.FilePickerFileType(Localizer.Instance?["playlist.ft_m3u"] ?? "M3U playlist (*.m3u)")
                    { Patterns = new[] { "*.m3u" } }
            }
        };

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(opts);
        if (file is null) return;

        var api      = App.Services.GetRequiredService<ApiClientService>();
        var filePath = file.Path.LocalPath;
        var items    = ViewModel.Items
            .Where(i => i.AssetId is not null)
            .ToList();

        var entries = new List<(string Title, string? Artist, uint DurationMs, string? AudioPath)>();
        foreach (var item in items)
        {
            string? audioPath = null;
            try
            {
                var detail = await api.GetAssetDetailAsync(item.AssetId!);
                audioPath  = detail?.RdmFilePath;
            }
            catch { /* skip path — entry still written without it */ }
            entries.Add((item.Title, item.Artist, item.DurationMs, audioPath));
        }

        await Services.PlaylistFileService.SaveM3uAsync(filePath, entries);
    }

}
