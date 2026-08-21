using Microsoft.AspNetCore.Mvc;
using RDM.Core.Interfaces;
using RDM.Shared.DTOs;
using System.Threading.Tasks;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/playlist")]
public sealed class PlaybackController : ControllerBase
{
    private readonly IPlaylistController _playlistController;
    private readonly IAssetRepository _assetRepository;

    public PlaybackController(
        IPlaylistController playlistController,
        IAssetRepository assetRepository)
    {
        _playlistController = playlistController;
        _assetRepository = assetRepository;
    }

    [HttpPost("play")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> Play()
    {
        await _playlistController.PlayAsync();
        var info = _playlistController.GetNowPlayingInfo();
        return Ok(new PlaybackStatusResponseDto("OK", info.State.ToString().ToUpperInvariant()));
    }

    [HttpPost("pause")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> Pause()
    {
        await _playlistController.PauseAsync();
        var info = _playlistController.GetNowPlayingInfo();
        return Ok(new PlaybackStatusResponseDto("OK", info.State.ToString().ToUpperInvariant()));
    }

    [HttpPost("stop")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> Stop()
    {
        await _playlistController.StopAsync();
        var info = _playlistController.GetNowPlayingInfo();
        return Ok(new PlaybackStatusResponseDto("OK", info.State.ToString().ToUpperInvariant()));
    }

    /// Repeat-current-track. Goes through the playlist controller, not the audio engine:
    /// looping is a playout decision (the queue must not advance), and rewinding the audio
    /// is only half of it.
    [HttpPost("loop")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> Loop([FromBody] AuxLoopRequestDto body)
    {
        await _playlistController.SetLoopCurrentAsync(body.Enabled);
        return Ok(new PlaybackStatusResponseDto("OK", body.Enabled ? "LOOP_ON" : "LOOP_OFF"));
    }

    [HttpPost("next")]
    public async Task<ActionResult<PlaybackNextResponseDto>> Next()
    {
        await _playlistController.NextTrackAsync();
        var info = _playlistController.GetNowPlayingInfo();
        if (info.CurrentAsset == null)
        {
            return NoContent();
        }
        return Ok(new PlaybackNextResponseDto("OK", info.CurrentAsset.AssetId, info.CurrentAsset.Title, info.CurrentAsset.Artist));
    }

    [HttpPost("reset")]
    public async Task<ActionResult<PlaybackResetResponseDto>> Reset()
    {
        await _playlistController.ResetAsync();
        var info = _playlistController.GetNowPlayingInfo();
        return Ok(new PlaybackResetResponseDto("OK", info.PositionMs));
    }

    [HttpPost("play/{assetId}")]
    public async Task<ActionResult<PlaybackPlaySpecificResponseDto>> PlaySpecific(string assetId)
    {
        var asset = await _assetRepository.GetByIdAsync(assetId);
        if (asset == null)
        {
            return NotFound(new ErrorResponseDto("ASSET_NOT_FOUND", "Asset with given ID does not exist.", HttpContext.TraceIdentifier));
        }

        if (asset.IsDamaged)
        {
            return UnprocessableEntity(new ErrorResponseDto("ASSET_DAMAGED", "Asset is marked as damaged and cannot be played.", HttpContext.TraceIdentifier));
        }

        await _playlistController.PlayAssetDirectlyAsync(assetId);
        return Ok(new PlaybackPlaySpecificResponseDto("OK", asset.AssetId, asset.Title, asset.Artist));
    }

    [HttpPost("pfl/{assetId}")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> StartPfl(string assetId, [FromServices] IAudioEngine audioEngine)
    {
        var asset = await _assetRepository.GetByIdAsync(assetId);
        if (asset == null)
            return NotFound();

        string? playPath = asset.AssetType == RDM.Shared.Enums.AssetType.InternetStream
            ? asset.StreamUrl
            : asset.RdmFilePath;

        if (playPath == null)
            return NotFound();

        await audioEngine.StartPflAsync(assetId, playPath);
        return Ok(new PlaybackStatusResponseDto("OK", "PFL_PLAYING"));
    }

    [HttpPost("pfl/file")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> StartPflFromFile(
        [FromBody] PflFromFileRequestDto body,
        [FromServices] IAudioEngine audioEngine)
    {
        if (!System.IO.File.Exists(body.FilePath))
            return NotFound(new ErrorResponseDto("FILE_NOT_FOUND", "Audio file not found on disk.", HttpContext.TraceIdentifier));

        await audioEngine.StartPflAsync("pfl-preview", body.FilePath);
        return Ok(new PlaybackStatusResponseDto("OK", "PFL_PLAYING"));
    }

    [HttpPost("pfl/stop")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> StopPfl([FromServices] IAudioEngine audioEngine)
    {
        await audioEngine.StopPflAsync();
        return Ok(new PlaybackStatusResponseDto("OK", "PFL_STOPPED"));
    }

    [HttpPost("pfl/seek")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> SeekPfl(
        [FromQuery] int offset_ms,
        [FromServices] IAudioEngine audioEngine)
    {
        await audioEngine.SeekPflAsync(offset_ms);
        return Ok(new PlaybackStatusResponseDto("OK", "PFL_SEEK"));
    }
}
