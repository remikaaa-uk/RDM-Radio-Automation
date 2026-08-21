using Microsoft.AspNetCore.Mvc;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/playlists")]
public sealed class SavedPlaylistsController : ControllerBase
{
    private readonly IPlaylistRepository _playlistRepository;
    private readonly IAssetRepository    _assetRepository;
    private readonly StudioContext       _studioContext;

    public SavedPlaylistsController(
        IPlaylistRepository playlistRepository,
        IAssetRepository    assetRepository,
        StudioContext       studioContext)
    {
        _playlistRepository = playlistRepository;
        _assetRepository    = assetRepository;
        _studioContext      = studioContext;
    }

    [HttpGet]
    public async Task<ActionResult<SavedPlaylistsEnvelopeDto>> GetPlaylists()
    {
        var playlists = await _playlistRepository.GetByStudioAsync(_studioContext.StudioId);

        var summaryTasks = playlists.Select(async p =>
        {
            var items = await _playlistRepository.GetItemsAsync(p.PlaylistId);
            return new SavedPlaylistSummaryDto(p.PlaylistId, p.Name, p.CreatedAt, items.Count);
        });

        var summaries = await Task.WhenAll(summaryTasks);
        return Ok(new SavedPlaylistsEnvelopeDto(summaries.ToList()));
    }

    [HttpGet("{playlistId}")]
    public async Task<ActionResult<SavedPlaylistDetailDto>> GetPlaylist(string playlistId)
    {
        var playlist = await _playlistRepository.GetByIdAsync(playlistId);
        if (playlist is null)
            return NotFound(new ErrorResponseDto("PLAYLIST_NOT_FOUND", "Playlist with given ID does not exist.", HttpContext.TraceIdentifier));

        var items = await _playlistRepository.GetItemsAsync(playlistId);

        var assetIds = items
            .Where(i => i.AssetId is not null)
            .Select(i => i.AssetId!)
            .Distinct()
            .ToList();

        var assetTasks = assetIds.Select(id => _assetRepository.GetByIdAsync(id));
        var assets     = await Task.WhenAll(assetTasks);
        var assetMap   = assets.Where(a => a is not null).ToDictionary(a => a!.AssetId, a => a!);

        var itemDtos = items.Select(item =>
        {
            Core.Entities.Asset? asset = item.AssetId is not null ? assetMap.GetValueOrDefault(item.AssetId) : null;

            return new PlaylistItemDto(
                ItemId:          item.ItemId,
                AssetId:         item.AssetId,
                Position:        item.Position,
                ItemType:        item.ItemType.ToString().ToUpperInvariant(),
                ExternalFilePath: item.ExternalFilePath,
                DummyLabel:      item.DummyLabel,
                DummyNote:       item.DummyNote,
                DummyDurationMs: item.DummyDurationMs,
                CrossfadeMs:     item.CrossfadeMs,
                LeadInMs:        item.LeadInMs,
                TrimStartMs:     item.TrimStartMs,
                TrimEndMs:       item.TrimEndMs,
                SegueType:       item.SegueType.ToString().ToUpperInvariant(),
                ScheduledAt:     item.ScheduledAt,
                AutoLinkNext:    item.AutoLinkNext,
                Title:           asset?.Title,
                Artist:          asset?.Artist,
                DurationMs:      asset?.DurationMs,
                AssetType:       asset?.AssetType.ToString().ToUpperInvariant(),
                FormatName:      asset?.FormatName,
                Status:          asset?.Status.ToString().ToUpperInvariant(),
                IsDamaged:       asset?.IsDamaged,
                Comments:        asset?.Comments,
                CueMarkers:      null
            );
        }).ToList();

        return Ok(new SavedPlaylistDetailDto(playlist.PlaylistId, playlist.Name, playlist.CreatedAt, itemDtos));
    }

    [HttpPost]
    public async Task<ActionResult<SavePlaylistResponseDto>> SavePlaylist(
        [FromBody] SavePlaylistRequestDto request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Name is required.", HttpContext.TraceIdentifier));

        var playlistId = Guid.NewGuid().ToString();
        var now        = DateTime.UtcNow;

        var playlist = new Playlist
        {
            PlaylistId   = playlistId,
            StudioId     = _studioContext.StudioId,
            Name         = request.Name,
            PlaylistType = PlaylistType.Saved,
            CreatedAt    = now
        };

        await _playlistRepository.CreateAsync(playlist);

        if (request.Items is not null)
        {
            for (int i = 0; i < request.Items.Count; i++)
            {
                var src  = request.Items[i];
                var item = new PlaylistItem
                {
                    ItemId       = Guid.NewGuid().ToString(),
                    PlaylistId   = playlistId,
                    AssetId      = src.AssetId,
                    Position     = (uint)i,
                    ItemType     = Enum.TryParse<PlaylistItemType>(src.ItemType, ignoreCase: true, out var t)
                                   ? t : PlaylistItemType.Asset,
                    DummyLabel      = src.DummyLabel,
                    DummyNote       = src.DummyNote,
                    DummyDurationMs = src.DummyDurationMs,
                    CrossfadeMs     = src.CrossfadeMs,
                    TrimStartMs     = src.TrimStartMs,
                    TrimEndMs       = src.TrimEndMs,
                    SegueType       = Enum.TryParse<SegueType>(src.SegueType, ignoreCase: true, out var s)
                                      ? s : SegueType.Auto,
                    AutoLinkNext    = src.AutoLinkNext,
                    VolumeEnvelope  = src.VolumeEnvelope
                };
                await _playlistRepository.AddItemAsync(item);
            }
        }

        return Ok(new SavePlaylistResponseDto(playlistId, request.Name, now));
    }

    [HttpPut("{playlistId}")]
    public async Task<ActionResult<SavePlaylistResponseDto>> UpdatePlaylist(
        string playlistId,
        [FromBody] SavePlaylistRequestDto request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Name is required.", HttpContext.TraceIdentifier));

        var playlist = await _playlistRepository.GetByIdAsync(playlistId);
        if (playlist is null)
            return NotFound(new ErrorResponseDto("PLAYLIST_NOT_FOUND", "Playlist with given ID does not exist.", HttpContext.TraceIdentifier));

        await _playlistRepository.UpdateNameAsync(playlistId, request.Name);
        await _playlistRepository.ClearItemsAsync(playlistId);

        if (request.Items is not null)
        {
            for (int i = 0; i < request.Items.Count; i++)
            {
                var src  = request.Items[i];
                var item = new PlaylistItem
                {
                    ItemId          = Guid.NewGuid().ToString(),
                    PlaylistId      = playlistId,
                    AssetId         = src.AssetId,
                    Position        = (uint)i,
                    ItemType        = Enum.TryParse<PlaylistItemType>(src.ItemType, ignoreCase: true, out var t)
                                      ? t : PlaylistItemType.Asset,
                    DummyLabel      = src.DummyLabel,
                    DummyNote       = src.DummyNote,
                    DummyDurationMs = src.DummyDurationMs,
                    CrossfadeMs     = src.CrossfadeMs,
                    TrimStartMs     = src.TrimStartMs,
                    TrimEndMs       = src.TrimEndMs,
                    SegueType       = Enum.TryParse<SegueType>(src.SegueType, ignoreCase: true, out var s)
                                      ? s : SegueType.Auto,
                    AutoLinkNext    = src.AutoLinkNext,
                    VolumeEnvelope  = src.VolumeEnvelope
                };
                await _playlistRepository.AddItemAsync(item);
            }
        }

        return Ok(new SavePlaylistResponseDto(playlistId, request.Name, playlist.CreatedAt));
    }

    [HttpDelete("{playlistId}")]
    public async Task<IActionResult> DeletePlaylist(string playlistId)
    {
        var playlist = await _playlistRepository.GetByIdAsync(playlistId);
        if (playlist is null)
            return NotFound(new ErrorResponseDto("PLAYLIST_NOT_FOUND", "Playlist with given ID does not exist.", HttpContext.TraceIdentifier));

        await _playlistRepository.DeleteAsync(playlistId);
        return NoContent();
    }
}
