using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Shared.DTOs;
using RDM.UI.Services;
using RDM.UI.Views;
using System.Collections.Generic;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

public sealed partial class PlaylistViewModel : ObservableObject, IDisposable
{
    private readonly ApiClientService         _api;
    private readonly IEventBus                _eventBus;
    private readonly CountdownService         _countdown;
    private readonly INavigationService       _navigationService;
    private readonly ILibrarySelectionService _selection;
    private readonly IInsertCursorService     _cursor;
    private readonly ILogger<PlaylistViewModel> _logger;

    private readonly Action<MicActivatedEvent>   _micActivatedHandler;
    private readonly Action<MicDeactivatedEvent> _micDeactivatedHandler;
    private readonly Action<PlayerLoopedEvent>   _loopedHandler;

    private DateTime _lastReloadAt;
    private const int ReloadCooldownMs = 100;

    // Cue points and duration of the currently playing track — captured in LoadPlaylistAsync /
    // OnTrackStarted so they survive the filtering step that removes the playing item from Items.
    private uint? _playingIntroCueMs;
    private uint? _playingOutroCueMs;
    private uint? _playingStartNextCueMs;
    private uint? _playingStartCueMs;
    private uint  _playingDurationMs;

    // ETA engine: remaining ms for the playing track, decremented by 1 Hz timer.
    private uint             _remainingCurrentMs;
    private DispatcherTimer? _etaTimer;

    // Number of server-side items preceding the visible list (= playingIdx + 1 when playing).
    // Required to convert UI drop indices to server positions correctly.
    private int _visibleOffset;

    // ── Observable state ──────────────────────────────────────────────────────

    public ObservableCollection<PlaylistItemViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoMode))]
    [NotifyPropertyChangedFor(nameof(IsAssistMode))]
    [NotifyPropertyChangedFor(nameof(IsManualMode))]
    private string _mode = "LIVE_ASSIST";

    [ObservableProperty] private PlaylistItemViewModel? _selectedItem;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _sessionState = "IDLE"; // IDLE | PLAYING | PAUSED
    [ObservableProperty] private bool _isMicActive;

    public bool IsAutoMode   => Mode == "AUTO";
    public bool IsAssistMode => Mode == "LIVE_ASSIST";
    public bool IsManualMode => Mode == "MANUAL";

    public CountdownService Countdown => _countdown;

    // ── Constructor ───────────────────────────────────────────────────────────

    private readonly RDM.Core.Entities.AudioSettings _audioSettings;

    public PlaylistViewModel(
        ApiClientService                api,
        IEventBus                       eventBus,
        CountdownService                countdown,
        INavigationService              navigationService,
        ILibrarySelectionService        selection,
        IInsertCursorService            cursor,
        RDM.Core.Entities.AudioSettings audioSettings,
        ILogger<PlaylistViewModel>      logger)
    {
        _api               = api;
        _eventBus          = eventBus;
        _countdown         = countdown;
        _navigationService = navigationService;
        _selection         = selection;
        _cursor            = cursor;
        _audioSettings     = audioSettings;
        _logger            = logger;

        _micActivatedHandler   = _ => Dispatcher.UIThread.Post(() => IsMicActive = true);
        _micDeactivatedHandler = _ => Dispatcher.UIThread.Post(() => IsMicActive = false);

        // A looping track rewound: restart the position clock in place. This VM owns the
        // countdown's lifecycle (see OnTrackStarted/OnTrackEnded), and the service is a
        // singleton, so the player's playhead and remaining-time follow from here too.
        _loopedHandler = e =>
        {
            var restartedAt = DateTime.UtcNow;
            Dispatcher.UIThread.Post(() =>
            {
                _countdown.OnLooped(restartedAt, e.PositionMs);
                RecalculateETAs();
            });
        };

        _etaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _etaTimer.Tick += (_, _) => OnEtaTick();
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Activate()
    {
        _api.TrackStarted        += OnTrackStarted;
        _api.TrackEnded          += OnTrackEnded;
        _api.PlaylistModeChanged += OnPlaylistModeChanged;
        _api.PlaylistStopped     += OnPlaylistStopped;
        _api.PlaylistUpdated     += OnPlaylistUpdated;

        _eventBus.Subscribe(_micActivatedHandler);
        _eventBus.Subscribe(_micDeactivatedHandler);
        _eventBus.Subscribe(_loopedHandler);

        _etaTimer?.Start();
        _ = LoadPlaylistAsync();
    }

    public void Dispose()
    {
        _etaTimer?.Stop();

        _api.TrackStarted        -= OnTrackStarted;
        _api.TrackEnded          -= OnTrackEnded;
        _api.PlaylistModeChanged -= OnPlaylistModeChanged;
        _api.PlaylistStopped     -= OnPlaylistStopped;
        _api.PlaylistUpdated     -= OnPlaylistUpdated;

        _eventBus.Unsubscribe(_micActivatedHandler);
        _eventBus.Unsubscribe(_micDeactivatedHandler);
        _eventBus.Unsubscribe(_loopedHandler);

        _countdown.OnTrackEnded();
    }

    private void OnPlaylistUpdated()
    {
        Dispatcher.UIThread.Post(() => _ = LoadPlaylistAsync());
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SetModeAutoAsync()    => await ChangeModeAsync("AUTO");

    [RelayCommand]
    private async Task SetModeAssistAsync()  => await ChangeModeAsync("LIVE_ASSIST");

    [RelayCommand]
    private async Task SetModeManualAsync()  => await ChangeModeAsync("MANUAL");

    [RelayCommand]
    private async Task ToggleMicAsync()
    {
        try
        {
            if (IsMicActive)
                await _api.StopMicAsync();
            else
                await _api.StartMicAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ToggleMicAsync failed (wasMicActive={Was})", IsMicActive);
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        if (SelectedItem is null) return;
        await RemoveItemAsync(SelectedItem.ItemId);
    }

    [RelayCommand]
    private async Task ClearPlaylistAsync()
    {
        await _api.ClearPlaylistAsync();
        LoadedSavedPlaylistId   = null;   // an empty queue is no longer "that playlist"
        LoadedSavedPlaylistName = null;
    }

    [RelayCommand]
    private async Task DropFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        var position = Items.Count + _visibleOffset;   // append after all visible items
        // Explorer drop → external item (played from disk, not imported). The server reads
        // ID3 tags at add time to fill title/artist/duration.
        await _api.AddExternalPlaylistItemAsync(
            new RDM.Shared.DTOs.AddExternalPlaylistItemRequestDto(filePath, position));
        await LoadPlaylistAsync();
    }

    public async Task AddAssetAsync(string assetId, int position)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return;
        await _api.AddPlaylistItemAsync(
            new RDM.Shared.DTOs.AddPlaylistItemRequestDto(assetId, null, position + _visibleOffset, "ASSET"));
        await LoadPlaylistAsync();
    }

    public async Task LoadSavedPlaylistAsync(string playlistId, bool appendToEnd)
    {
        IsBusy = true;
        try
        {
            var detail = await _api.GetSavedPlaylistDetailAsync(playlistId);
            if (detail is null) return;

            // Remembered only to preselect it as the overwrite target on the next save —
            // the queue may well hold more than this playlist, so the save dialog still asks.
            LoadedSavedPlaylistId   = detail.PlaylistId;
            LoadedSavedPlaylistName = detail.Name;

            int position = appendToEnd ? Items.Count + _visibleOffset : _visibleOffset;
            foreach (var item in detail.Items)
            {
                if (item.AssetId is null) continue;
                await _api.AddPlaylistItemAsync(
                    new AddPlaylistItemRequestDto(item.AssetId, null, position++, "ASSET"));
            }

            await LoadPlaylistAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load saved playlist failed"); }
        finally { IsBusy = false; }
    }

    public async Task LoadFromM3uAsync(string filePath, bool appendToEnd)
    {
        IsBusy = true;
        try
        {
            var entries = await Services.PlaylistFileService.LoadM3uAsync(filePath);
            if (entries.Count == 0) return;

            // An M3U has no database identity — stop offering the previous playlist as
            // the overwrite target once its content is mixed with external files.
            LoadedSavedPlaylistId   = null;
            LoadedSavedPlaylistName = null;

            // Every M3U entry is added as an external item — played straight from its path,
            // never imported into the library. Title/artist come from #EXTINF (server reads
            // ID3 as a fallback). Relative paths resolve against the M3U file's folder.
            var baseDir = System.IO.Path.GetDirectoryName(filePath) ?? string.Empty;

            int position = appendToEnd ? Items.Count + _visibleOffset : _visibleOffset;
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.FilePath)) continue;

                var trackPath = System.IO.Path.IsPathRooted(entry.FilePath)
                    ? entry.FilePath
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, entry.FilePath));

                uint? durationMs = entry.DurationSec > 0 ? (uint)(entry.DurationSec * 1000) : null;

                await _api.AddExternalPlaylistItemAsync(
                    new AddExternalPlaylistItemRequestDto(trackPath, position++, entry.Title, entry.Artist, durationMs));
            }

            await LoadPlaylistAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "LoadFromM3u failed"); }
        finally { IsBusy = false; }
    }

    /// Playlist the queue was last loaded from / saved as. Drives the default
    /// overwrite target in the save dialog; null means "nothing to overwrite".
    public string? LoadedSavedPlaylistId   { get; private set; }
    public string? LoadedSavedPlaylistName { get; private set; }

    /// <param name="TotalCount">Everything the engine holds, played tracks included.</param>
    /// <param name="SavableCount">Library items — the only ones the database can store.</param>
    /// <param name="ExternalCount">Items played straight from disk (M3U / drag &amp; drop); dropped on save.</param>
    public readonly record struct PlaylistSaveSummary(int TotalCount, int SavableCount, int ExternalCount);

    /// What a save would actually write — used to warn before an overwrite.
    public async Task<PlaylistSaveSummary> GetSaveSummaryAsync()
    {
        try
        {
            var envelope = await _api.GetPlaylistItemsAsync();
            if (envelope is null) return default;

            int total   = envelope.Items.Count;
            int savable = envelope.Items.Count(i => i.AssetId is not null);
            return new PlaylistSaveSummary(total, savable, total - savable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetSaveSummary failed");
            return default;
        }
    }

    // Called from PlaylistView code-behind after the user picks a name and a target.
    // overwriteId != null replaces that saved playlist (PUT); otherwise a new one is created.
    public async Task<bool> SaveCurrentPlaylistAsync(string name, string? overwriteId = null)
    {
        IsBusy = true;
        try
        {
            // Fetch all items from engine (includes currently playing track).
            var envelope = await _api.GetPlaylistItemsAsync();
            if (envelope is null) return false;

            var saveItems = envelope.Items
                .Where(i => i.AssetId is not null)
                .Select(i => new PlaylistItemSaveDto(
                    AssetId:         i.AssetId,
                    ItemType:        "ASSET",
                    DummyLabel:      null,
                    DummyNote:       null,
                    DummyDurationMs: null,
                    CrossfadeMs:     i.CrossfadeMs,
                    TrimStartMs:     i.TrimStartMs,
                    TrimEndMs:       i.TrimEndMs,
                    SegueType:       i.SegueType,
                    AutoLinkNext:    i.AutoLinkNext))
                .ToList();

            var dto  = new SavePlaylistRequestDto(name, saveItems);
            var resp = overwriteId is not null
                ? await _api.UpdateSavedPlaylistAsync(overwriteId, dto)
                : await _api.SavePlaylistAsync(dto);

            if (resp is null)
            {
                _logger.LogWarning("Save playlist returned no response (overwriteId={Id})", overwriteId);
                return false;
            }

            LoadedSavedPlaylistId   = resp.PlaylistId;
            LoadedSavedPlaylistName = resp.Name;
            return true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Save playlist failed"); return false; }
        finally { IsBusy = false; }
    }

    public Task<SavedPlaylistsEnvelopeDto?> GetSavedPlaylistsAsync()
        => _api.GetSavedPlaylistsAsync();

    // ── Item actions (called by PlaylistItemViewModel commands) ───────────────

    public async Task RemoveItemAsync(string itemId)
    {
        await _api.RemovePlaylistItemAsync(itemId);
        await LoadPlaylistAsync();
    }

    public async void InsertAboveAsync(string itemId)
    {
        if (_selection.SelectedAssetId is not string assetId) return;
        var vm = Items.FirstOrDefault(x => x.ItemId == itemId);
        if (vm is null) return;
        int visibleIdx = Items.IndexOf(vm);
        await AddAssetAsync(assetId, visibleIdx);
    }

    public async void InsertBelowAsync(string itemId)
    {
        if (_selection.SelectedAssetId is not string assetId) return;
        var vm = Items.FirstOrDefault(x => x.ItemId == itemId);
        if (vm is null) return;
        int visibleIdx = Items.IndexOf(vm);
        await AddAssetAsync(assetId, visibleIdx + 1);
    }

    // ── Insert cursor ─────────────────────────────────────────────────────────

    public bool IsCursorActive => _cursor.IsActive;

    [RelayCommand]
    private void ClearCursor()
    {
        _cursor.Clear();
        ApplyCursorToItems();
        OnPropertyChanged(nameof(IsCursorActive));
    }

    public void SetCursorAtItem(string itemId)
    {
        var vm = Items.FirstOrDefault(x => x.ItemId == itemId);
        if (vm is null) return;
        _cursor.SetIndex(Items.IndexOf(vm));
        ApplyCursorToItems();
        OnPropertyChanged(nameof(IsCursorActive));
    }

    private void ApplyCursorToItems()
    {
        int? idx = _cursor.IsActive ? _cursor.VisibleIndex : null;
        for (int i = 0; i < Items.Count; i++)
            Items[i].IsCursorPosition = idx.HasValue && i == idx.Value;
    }

    private string? _currentPflAssetId;

    public async void PflItemAsync(string itemId)
    {
        var item = Items.FirstOrDefault(i => i.ItemId == itemId);
        if (item?.AssetId == null) return;

        if (_currentPflAssetId == item.AssetId)
        {
            await _api.StopPflAsync();
            _currentPflAssetId = null;
        }
        else
        {
            await _api.StartPflAsync(item.AssetId);
            _currentPflAssetId = item.AssetId;
        }
    }

    // ── Drag & Drop reorder ───────────────────────────────────────────────────

    public async Task ReorderItemAsync(string itemId, int newPosition)
    {
        await _api.ReorderPlaylistItemAsync(
            new RDM.Shared.DTOs.ReorderPlaylistItemRequestDto(itemId, newPosition + _visibleOffset));
        await LoadPlaylistAsync();
    }

    // ── ETA engine ────────────────────────────────────────────────────────────

    private void OnEtaTick()
    {
        if (SessionState == "PLAYING")
            _remainingCurrentMs = _remainingCurrentMs >= 1000 ? _remainingCurrentMs - 1000 : 0;

        ApplyETAs();
    }

    // Full recalculation — reads current position from CountdownService.
    // Call on structural changes: track start/end, add/remove/reorder, playlist stop, Activate.
    private void RecalculateETAs()
    {
        if (SessionState is "PLAYING" or "PAUSED")
        {
            var effectiveEndMs = _playingStartNextCueMs ?? _playingDurationMs;
            var posMs = _countdown.PositionMs;
            _remainingCurrentMs = effectiveEndMs > posMs ? effectiveEndMs - posMs : 0;
        }
        else
        {
            _remainingCurrentMs = 0;
        }

        ApplyETAs();
    }

    // Applies current _remainingCurrentMs to all visible items (snapshot to avoid mid-merge mutation).
    private void ApplyETAs()
    {
        var items = Items.ToList();
        if (items.Count == 0) return;

        bool isEstimated = SessionState is not ("PLAYING" or "PAUSED");
        var eta = DateTime.Now.AddMilliseconds(_remainingCurrentMs);

        foreach (var item in items)
        {
            item.ScheduledAtText = FormatEta(eta);
            item.IsEtaEstimated  = isEstimated;
            var step = item.StartNextCueMs ?? item.DurationMs;
            eta = eta.AddMilliseconds(step > 0 ? step : 180_000);
        }
    }

    private static string FormatEta(DateTime eta) => eta.ToString("HH:mm:ss");

    // ── WebSocket event handlers ──────────────────────────────────────────────

    private async void OnTrackStarted(TrackStartedPayload p)
    {
        var startedAt = DateTime.UtcNow;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            _playingDurationMs = p.DurationMs;
            // Reset countdown early so PositionMs≈0 when RecalculateETAs runs inside
            // LoadPlaylistAsync. Without this, PositionMs still holds the old track's
            // elapsed position, making _remainingCurrentMs = newDuration - oldPos ≈ 0.
            _countdown.OnTrackStarted(startedAt, p.DurationMs, null, null, 0);
            await LoadPlaylistAsync();
            // Re-apply now that the playing track's cue markers are populated. CueStart matters
            // here: playback begins there, so the position must be offset by it to stay aligned
            // with the (absolute) cue markers and the waveform.
            _countdown.OnTrackStarted(
                startedAt, p.DurationMs, _playingIntroCueMs, _playingOutroCueMs, _playingStartCueMs ?? 0);
        });
    }

    private void OnTrackEnded(TrackEndedPayload _)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _countdown.OnTrackEnded();
            SessionState = "IDLE";
            RecalculateETAs();
        });
    }

    private void OnPlaylistModeChanged(PlaylistModeChangedPayload p)
    {
        if (p.Mode is null) return;
        Dispatcher.UIThread.Post(() => Mode = p.Mode.ToUpperInvariant());
    }

    private void OnPlaylistStopped()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            _countdown.OnTrackEnded();
            foreach (var item in Items)
            {
                item.IsPlaying = false;
                item.IsNext    = false;
            }
            await LoadPlaylistAsync();
        });
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private async Task LoadPlaylistAsync()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastReloadAt).TotalMilliseconds < ReloadCooldownMs) return;
        _lastReloadAt = now;

        IsBusy = true;
        try
        {
            var envelope = await _api.GetPlaylistItemsAsync();
            if (envelope is null) return;

            Mode = envelope.Mode;
            SessionState = envelope.State;

            var allItems = envelope.Items.ToList();
            var playingIdx = allItems.FindIndex(i => i.ItemId == envelope.CurrentItemId);

            var visibleItems = allItems;
            if (playingIdx >= 0)
            {
                var playingDto = allItems[playingIdx];
                _playingIntroCueMs     = playingDto.CueMarkers?.Intro     is double intro      ? (uint)(intro      * 1000) : null;
                _playingOutroCueMs     = playingDto.CueMarkers?.Outro     is double outro      ? (uint)(outro      * 1000) : null;
                _playingStartNextCueMs = playingDto.CueMarkers?.StartNext is double startNext  ? (uint)(startNext  * 1000) : null;
                _playingStartCueMs     = playingDto.CueMarkers?.Start     is double start      ? (uint)(start      * 1000) : null;
                _visibleOffset = playingIdx + 1;
                visibleItems   = allItems.Skip(_visibleOffset).ToList();
            }
            else
            {
                _playingIntroCueMs     = null;
                _playingOutroCueMs     = null;
                _playingStartNextCueMs = null;
                _playingStartCueMs     = null;
                _visibleOffset         = 0;
            }

            var newMap = visibleItems.ToDictionary(d => d.ItemId);

            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (!newMap.ContainsKey(Items[i].ItemId))
                    Items.RemoveAt(i);
            }

            // Reconcile order/content to match visibleItems exactly. Reuse existing VMs
            // by ItemId — MOVING them when reordered — so a reordered row is never
            // duplicated (the old merge inserted a new VM and left the original at the
            // end). Items before index i are already finalized, so any match is at j > i.
            for (int i = 0; i < visibleItems.Count; i++)
            {
                var dto = visibleItems[i];
                var expectedPos = (uint)(i + 1);

                if (i < Items.Count && Items[i].ItemId == dto.ItemId)
                {
                    Items[i].UpdateFrom(dto, expectedPos);
                    continue;
                }

                int existingIdx = -1;
                for (int j = i + 1; j < Items.Count; j++)
                {
                    if (Items[j].ItemId == dto.ItemId) { existingIdx = j; break; }
                }

                if (existingIdx >= 0)
                {
                    Items.Move(existingIdx, i);
                    Items[i].UpdateFrom(dto, expectedPos);
                }
                else
                {
                    var vm = new PlaylistItemViewModel(
                        dto,
                        id => { _ = RemoveItemAsync(id); },
                        PflItemAsync,
                        OpenTrackEditor,
                        OpenSegueEditor,
                        onInsertAbove: InsertAboveAsync,
                        onInsertBelow: InsertBelowAsync,
                        onSetCursor:   SetCursorAtItem,
                        selection:     _selection,
                        hasPflDevice:  !string.IsNullOrEmpty(_audioSettings.DevicePflId));
                    vm.Position = expectedPos;
                    Items.Insert(i, vm);
                }
            }

            // Trim any leftover rows beyond the new list length.
            while (Items.Count > visibleItems.Count)
                Items.RemoveAt(Items.Count - 1);

            if (playingIdx >= 0 && Items.Count > 0)
            {
                Items[0].IsNext = true;
                for (int i = 1; i < Items.Count; i++) Items[i].IsNext = false;
            }
            else
            {
                foreach (var i in Items) i.IsNext = false;
            }

            RecalculateETAs();
            ApplyCursorToItems();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load playlist items");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void OpenTrackEditor(string? assetId)
    {
        if (assetId == null) return;
        var ids = Items
            .Where(i => i.AssetId is not null)
            .Select(i => i.AssetId!)
            .ToList();
        var idx = ids.IndexOf(assetId);
        var ctx = new TrackEditorNavContext(ids, Math.Max(0, idx));
        await _navigationService.ShowModalAsync<TrackEditorWindow>(ctx);
    }

    // Opens the mix editor on the transition between the selected live item and the
    // next one. Unlike the builder, live items are persisted, so segue changes are
    // patched straight to the ON_AIR queue when the editor closes.
    private async void OpenSegueEditor(string itemId)
    {
        try
        {
            var envelope = await _api.GetPlaylistItemsAsync();
            if (envelope is null) return;

            var all = envelope.Items.ToList();
            var idx = all.FindIndex(i => i.ItemId == itemId);
            if (idx < 0 || all.Count < 2) return;   // need at least one neighbouring transition

            // Open the editor on the clicked track in context: its predecessor and
            // successor, clamped to the queue bounds ([item, next] at the head,
            // [prev, item] at the tail, [prev, item, next] in the middle). This shows
            // both transitions of the clicked track, not just the outgoing one.
            int start = Math.Max(0, idx - 1);
            int end   = Math.Min(all.Count - 1, idx + 1);

            var items  = all.GetRange(start, end - start + 1).ToArray();
            var window = await _navigationService.ShowModalAsync<SegueEditorWindow>(items);

            if (window.DataContext is not SegueEditorViewModel segueVm) return;

            // liveIndex tracks the 0-based slot in the full queue for the next clip.
            // The first editor clip is all[start]; new (voice-track) clips are inserted
            // at their current slot so they land between the right neighbours.
            int liveIndex = start;
            for (int i = 0; i < segueVm.Clips.Count; i++)
            {
                var clip = segueVm.Clips[i];

                if (clip.IsNew)
                {
                    if (clip.AssetId is null) continue;   // import failed → nothing to add
                    await _api.AddPlaylistItemAsync(
                        new AddPlaylistItemRequestDto(clip.AssetId, null, liveIndex, "ASSET"));
                    liveIndex++;
                    continue;
                }

                var patch = new PatchPlaylistItemDto(
                    CrossfadeMs:    i > 0 ? clip.CrossfadeMs : null,
                    LeadInMs:       i > 0 ? clip.LeadInMs    : null,
                    TrimStartMs:    clip.TrimStartMs > 0 ? clip.TrimStartMs : null,
                    TrimEndMs:      clip.TrimEndMs   > 0 ? clip.TrimEndMs   : null,
                    VolumeEnvelope: clip.VolumeEnvelope.Count > 0
                                    ? JsonSerializer.Serialize(clip.VolumeEnvelope.ToList())
                                    : null);
                await _api.PatchPlaylistItemAsync(clip.ItemId, patch);
                liveIndex++;
            }

            await LoadPlaylistAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Segue editor (live) failed"); }
    }

    private async Task ChangeModeAsync(string newMode)
    {
        await _api.ChangePlaylistModeAsync(newMode);
        Mode = newMode;
    }
}
