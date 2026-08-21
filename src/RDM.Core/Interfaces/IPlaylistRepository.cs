using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IPlaylistRepository
{
    Task<Playlist?> GetByIdAsync(string playlistId, CancellationToken ct = default);
    Task<IReadOnlyList<Playlist>> GetByStudioAsync(string studioId, CancellationToken ct = default);
    Task<Playlist?> GetOnAirPlaylistAsync(string studioId, CancellationToken ct = default);
    Task<IReadOnlyList<PlaylistItem>> GetItemsAsync(string playlistId, CancellationToken ct = default);
    Task CreateAsync(Playlist playlist, CancellationToken ct = default);
    Task DeleteAsync(string playlistId, CancellationToken ct = default);
    Task UpdateNameAsync(string playlistId, string name, CancellationToken ct = default);
    Task ClearItemsAsync(string playlistId, CancellationToken ct = default);
    Task AddItemAsync(PlaylistItem item, CancellationToken ct = default);
    Task UpdateItemAsync(PlaylistItem item, CancellationToken ct = default);
    Task RemoveItemAsync(string itemId, CancellationToken ct = default);
}
