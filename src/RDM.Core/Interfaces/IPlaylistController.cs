using RDM.Core.Entities;
using RDM.Core.Models;
using RDM.Shared.Enums;

namespace RDM.Core.Interfaces;

/// <summary>
/// Operations on the active playlist consumed by EventScheduler and SweeperEngine.
/// Extracted as an interface to allow mocking in tests.
/// </summary>
public interface IPlaylistController
{
    bool IsPlaying { get; }

    /// <summary>Returns the item immediately after the current one, or null at end of playlist.</summary>
    PlaylistItem? PeekNextItem();

    Task ClearAsync(CancellationToken ct = default);
    Task LoadPlaylistAsync(string playlistId, CancellationToken ct = default);

    /// Loads a SAVED playlist into the live queue by copying its tracks into the studio's
    /// ON_AIR playlist (see PlaylistEngine.LoadSavedPlaylistIntoQueueAsync for why this is
    /// distinct from LoadPlaylistAsync). Used by the LOAD_PLAYLIST scheduled-event action.
    Task LoadSavedPlaylistIntoQueueAsync(string savedPlaylistId, CancellationToken ct = default);
    Task PlayAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task NextTrackAsync(CancellationToken ct = default);

    /// Repeat-current-track. While enabled the current item plays over and over and the
    /// playlist does not advance on its own — in EVERY mode, AUTO included. Manual Next/Prev
    /// still work and the loop then applies to the newly loaded item; Stop leaves it armed.
    /// Safe to call with nothing loaded: it takes effect on the next track that starts.
    Task SetLoopCurrentAsync(bool enabled, CancellationToken ct = default);
    Task ResetAsync(CancellationToken ct = default);
    Task ChangeModeAsync(PlaylistMode newMode, CancellationToken ct = default);

    /// Hot-swaps the cached audio settings (crossfade duration, sweeper ducking level)
    /// so changes saved in Settings apply to subsequent transitions without a restart.
    /// Does not alter the live playback mode — DefaultMode only seeds the mode at startup.
    Task UpdateAudioSettingsAsync(AudioSettings settings, CancellationToken ct = default);
    Task PlayAssetDirectlyAsync(string assetId, CancellationToken ct = default);
    NowPlayingInfo GetNowPlayingInfo();

    // ── Live playlist editing (UI-002B) ───────────────────────────────────────
    Task<IReadOnlyList<PlaylistItem>> GetCurrentItemsAsync(CancellationToken ct = default);
    Task<string> AddItemAsync(string assetId, int position, CancellationToken ct = default);
    Task<string> AddExternalItemAsync(string filePath, string? title, string? artist, uint? durationMs, int position, CancellationToken ct = default);
    Task RemoveItemAsync(string itemId, CancellationToken ct = default);
    Task RemoveCurrentItemAsync(CancellationToken ct = default);
    Task ReorderItemAsync(string itemId, int newPosition, CancellationToken ct = default);
    Task PatchItemAsync(string itemId, uint? crossfadeMs, int? leadInMs, uint? trimStartMs, uint? trimEndMs, string? segueType, bool? autoLinkNext, string? volumeEnvelope = null, CancellationToken ct = default);
}
