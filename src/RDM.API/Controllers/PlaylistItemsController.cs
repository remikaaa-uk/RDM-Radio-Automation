using Microsoft.AspNetCore.Mvc;
using RDM.API.Mappers;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Infrastructure.FileSystem;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/playlist")]
public sealed class PlaylistItemsController : ControllerBase
{
    private readonly IPlaylistController _playlistController;
    private readonly IAssetRepository    _assetRepository;
    private readonly IImportPipeline     _importPipeline;
    private readonly Id3TagReader        _id3;

    public PlaylistItemsController(
        IPlaylistController          playlistController,
        IAssetRepository             assetRepository,
        IImportPipeline              importPipeline,
        IEnumerable<IMetadataReader> metadataReaders)
    {
        _playlistController = playlistController;
        _assetRepository    = assetRepository;
        _importPipeline     = importPipeline;
        _id3                = metadataReaders.OfType<Id3TagReader>().First();
    }

    [HttpGet("items")]
    public async Task<ActionResult<PlaylistItemsEnvelopeDto>> GetItems()
    {
        var items = await _playlistController.GetCurrentItemsAsync();
        var info  = _playlistController.GetNowPlayingInfo();

        // Batch-load assets for all ASSET items.
        var assetIds = items
            .Where(i => i.AssetId is not null)
            .Select(i => i.AssetId!)
            .Distinct()
            .ToList();

        var assets   = await Task.WhenAll(assetIds.Select(id => _assetRepository.GetByIdAsync(id)));
        var assetMap = assets.Where(a => a is not null).ToDictionary(a => a!.AssetId, a => a!);

        var dtos = items.Select(item => MapItem(item, assetMap)).ToList();

        return Ok(new PlaylistItemsEnvelopeDto(
            Items:         dtos,
            Mode:          MapMode(info.Mode),
            State:         info.State.ToString().ToUpperInvariant(),
            CurrentItemId: info.CurrentItemId
        ));
    }

    [HttpPost("items")]
    public async Task<ActionResult<AddPlaylistItemResponseDto>> AddItem(
        [FromBody] AddPlaylistItemRequestDto request)
    {
        if (request is null)
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Request body is required.", HttpContext.TraceIdentifier));

        string itemId;
        try
        {
            if (!string.IsNullOrWhiteSpace(request.ExternalFilePath))
            {
                var importResult = await _importPipeline.ImportAsync(request.ExternalFilePath, null, null, null, HttpContext.RequestAborted);
                string assetId = importResult switch
                {
                    ImportResult.Success s   => s.Asset.AssetId,
                    ImportResult.Duplicate d => d.AssetId,
                    ImportResult.Failed f    => null!,
                    _                        => null!
                };
                if (importResult is ImportResult.Failed failed)
                    return UnprocessableEntity(new ErrorResponseDto("IMPORT_FAILED", failed.Reason, HttpContext.TraceIdentifier));

                itemId = await _playlistController.AddItemAsync(assetId, request.Position);
            }
            else if (!string.IsNullOrWhiteSpace(request.AssetId))
            {
                var asset = await _assetRepository.GetByIdAsync(request.AssetId);
                if (asset is null)
                    return NotFound(new ErrorResponseDto("ASSET_NOT_FOUND", "Asset with given ID does not exist.", HttpContext.TraceIdentifier));

                itemId = await _playlistController.AddItemAsync(request.AssetId, request.Position);
            }
            else
            {
                return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Either AssetId or ExternalFilePath is required.", HttpContext.TraceIdentifier));
            }
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponseDto("NO_PLAYLIST", ex.Message, HttpContext.TraceIdentifier));
        }

        return Ok(new AddPlaylistItemResponseDto(itemId, (uint)request.Position));
    }

    [HttpPost("items/external")]
    public async Task<ActionResult<AddPlaylistItemResponseDto>> AddExternalItem(
        [FromBody] AddExternalPlaylistItemRequestDto request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.FilePath))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "FilePath is required.", HttpContext.TraceIdentifier));

        if (!System.IO.File.Exists(request.FilePath))
            return NotFound(new ErrorResponseDto("FILE_NOT_FOUND", "Audio file not found on disk.", HttpContext.TraceIdentifier));

        var title      = request.Title;
        var artist     = request.Artist;
        var durationMs = request.DurationMs;

        // Read ID3 at add time when metadata was not supplied (Explorer drag & drop).
        if (string.IsNullOrWhiteSpace(title) || durationMs is null)
        {
            var meta = await _id3.TryReadAsync(request.FilePath, HttpContext.RequestAborted);
            if (meta is not null)
            {
                title      ??= meta.Title;
                artist     ??= meta.Artist;
                durationMs ??= meta.DurationMs;
            }
        }

        string itemId;
        try
        {
            itemId = await _playlistController.AddExternalItemAsync(
                request.FilePath, title, artist, durationMs, request.Position);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponseDto("NO_PLAYLIST", ex.Message, HttpContext.TraceIdentifier));
        }

        return Ok(new AddPlaylistItemResponseDto(itemId, (uint)request.Position));
    }

    [HttpDelete("items")]
    public async Task<IActionResult> ClearItems()
    {
        await _playlistController.ClearAsync();
        return NoContent();
    }

    [HttpDelete("items/{itemId}")]
    public async Task<IActionResult> RemoveItem(string itemId)
    {
        try
        {
            await _playlistController.RemoveItemAsync(itemId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponseDto("NO_PLAYLIST", ex.Message, HttpContext.TraceIdentifier));
        }

        return NoContent();
    }

    [HttpDelete("current")]
    public async Task<IActionResult> RemoveCurrentItem()
    {
        await _playlistController.RemoveCurrentItemAsync();
        return NoContent();
    }

    [HttpPatch("items/reorder")]
    public async Task<IActionResult> ReorderItem(
        [FromBody] ReorderPlaylistItemRequestDto request)
    {
        if (request is null)
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Request body is required.", HttpContext.TraceIdentifier));

        try
        {
            await _playlistController.ReorderItemAsync(request.ItemId, request.NewPosition);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponseDto("NO_PLAYLIST", ex.Message, HttpContext.TraceIdentifier));
        }

        return NoContent();
    }

    [HttpPatch("items/{itemId}")]
    public async Task<IActionResult> PatchItem(string itemId, [FromBody] PatchPlaylistItemDto request)
    {
        if (request is null)
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Request body is required.", HttpContext.TraceIdentifier));

        try
        {
            await _playlistController.PatchItemAsync(
                itemId,
                request.CrossfadeMs,
                request.LeadInMs,
                request.TrimStartMs,
                request.TrimEndMs,
                request.SegueType,
                request.AutoLinkNext,
                request.VolumeEnvelope);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponseDto("NO_PLAYLIST", ex.Message, HttpContext.TraceIdentifier));
        }

        return NoContent();
    }

    [HttpPost("mode")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> ChangeMode(
        [FromBody] ChangePlaylistModeRequestDto request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Mode))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Mode is required.", HttpContext.TraceIdentifier));

        if (!Enum.TryParse<PlaylistMode>(request.Mode, ignoreCase: true, out var mode))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Invalid mode. Allowed: AUTO, LIVE_ASSIST, MANUAL", HttpContext.TraceIdentifier));

        await _playlistController.ChangeModeAsync(mode);
        return Ok(new PlaybackStatusResponseDto("OK", request.Mode.ToUpperInvariant()));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PlaylistItemDto MapItem(PlaylistItem item, Dictionary<string, Asset> assetMap)
    {
        Asset? asset = item.AssetId is not null ? assetMap.GetValueOrDefault(item.AssetId) : null;

        // External (non-library) item: no asset row — played straight from disk. Its title/
        // artist/duration were captured at add time into the dummy_* columns.
        bool isExternal = asset is null && item.AssetId is null && item.ExternalFilePath is not null;

        bool isStream = asset?.AssetType == RDM.Shared.Enums.AssetType.InternetStream;
        bool isDamaged = asset?.IsDamaged == true
            || (item.AssetId is not null && asset is null)
            || (!isStream && asset?.RdmFilePath is null && item.AssetId is not null)
            || (!isStream && asset?.RdmFilePath is not null && !System.IO.File.Exists(asset.RdmFilePath))
            || (isExternal && !System.IO.File.Exists(item.ExternalFilePath!));

        return new PlaylistItemDto(
            ItemId:           item.ItemId,
            AssetId:          item.AssetId,
            Position:         item.Position,
            ItemType:         item.ItemType.ToString().ToUpperInvariant(),
            ExternalFilePath: item.ExternalFilePath,
            DummyLabel:       item.DummyLabel,
            DummyNote:        item.DummyNote,
            DummyDurationMs:  item.DummyDurationMs,
            CrossfadeMs:      item.CrossfadeMs,
            LeadInMs:         item.LeadInMs,
            TrimStartMs:      item.TrimStartMs,
            TrimEndMs:        item.TrimEndMs,
            SegueType:        item.SegueType.ToString().ToUpperInvariant(),
            ScheduledAt:      item.ScheduledAt,
            AutoLinkNext:     item.AutoLinkNext,
            Title:            asset?.Title      ?? (isExternal ? (item.DummyLabel ?? Path.GetFileNameWithoutExtension(item.ExternalFilePath!)) : null),
            Artist:           asset?.Artist     ?? (isExternal ? item.DummyNote : null),
            DurationMs:       asset?.DurationMs  ?? (isExternal ? item.DummyDurationMs : null),
            AssetType:        asset?.AssetType.ToString().ToUpperInvariant() ?? (isExternal ? "TRACK" : null),
            FormatName:       asset?.FormatName,
            Status:           asset?.Status.ToString().ToUpperInvariant(),
            IsDamaged:        isDamaged,
            Comments:         asset?.Comments,
            CueMarkers:       asset is not null ? DtoMapper.BuildCueMarkers(asset) : null,
            VolumeEnvelope:   item.VolumeEnvelope
        );
    }

    private static string MapMode(PlaylistMode mode) => mode switch
    {
        PlaylistMode.Auto        => "AUTO",
        PlaylistMode.LiveAssist  => "LIVE_ASSIST",
        PlaylistMode.Manual      => "MANUAL",
        _                        => mode.ToString().ToUpperInvariant()
    };
}
