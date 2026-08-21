using Microsoft.AspNetCore.Mvc;
using RDM.API.Mappers;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.DTOs;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/nowplaying")]
public sealed class NowPlayingController : ControllerBase
{
    private readonly IPlaylistController _playlistController;
    private readonly IAssetRepository _assetRepository;
    private readonly IPlayoutLogRepository _playoutLogRepository;
    private readonly StudioContext _studioContext;

    public NowPlayingController(
        IPlaylistController playlistController,
        IAssetRepository assetRepository,
        IPlayoutLogRepository playoutLogRepository,
        StudioContext studioContext)
    {
        _playlistController = playlistController;
        _assetRepository = assetRepository;
        _playoutLogRepository = playoutLogRepository;
        _studioContext = studioContext;
    }

    [HttpGet]
    public async Task<ActionResult<NowPlayingDto>> GetNowPlaying()
    {
        var info = _playlistController.GetNowPlayingInfo();

        RDM.Core.Entities.Asset? nextAsset = null;
        if (info.NextItem != null && !string.IsNullOrEmpty(info.NextItem.AssetId))
            nextAsset = await _assetRepository.GetByIdAsync(info.NextItem.AssetId);

        var dto = DtoMapper.ToDto(info, nextAsset);
        return Ok(dto);
    }

    [HttpGet("next")]
    public async Task<ActionResult<NextTrackDto>> GetNextTrack()
    {
        var info = _playlistController.GetNowPlayingInfo();
        if (info.NextItem == null || string.IsNullOrEmpty(info.NextItem.AssetId))
        {
            return NoContent();
        }

        var nextAsset = await _assetRepository.GetByIdAsync(info.NextItem.AssetId);
        if (nextAsset == null)
        {
            return NoContent();
        }

        DateTime? scheduledAt = null;
        if (info.TrackStartedAt.HasValue && info.CurrentAsset != null)
        {
            var outroMs = info.OutroCueMs ?? info.CurrentAsset.DurationMs;
            scheduledAt = info.TrackStartedAt.Value.AddMilliseconds(outroMs);
        }

        var dto = DtoMapper.ToNextTrackDto(nextAsset, scheduledAt);
        return Ok(dto);
    }

    [HttpGet("history")]
    public async Task<ActionResult<PlayoutHistoryResponseDto>> GetHistory(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        if (limit < 1 || limit > 500)
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Limit must be between 1 and 500.", HttpContext.TraceIdentifier));
        if (offset < 0)
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Offset must be non-negative.", HttpContext.TraceIdentifier));

        var total = await _playoutLogRepository.CountAsync(_studioContext.StudioId);
        var logs  = await _playoutLogRepository.GetRecentAsync(_studioContext.StudioId, limit, offset);
        var dtos  = logs.Select(DtoMapper.ToDto).ToList();

        return Ok(new PlayoutHistoryResponseDto(dtos, total));
    }
}
