using Microsoft.AspNetCore.Mvc;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.DTOs;
using System.Threading.Tasks;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/aux")]
public sealed class AuxController : ControllerBase
{
    private readonly IAudioEngine             _audioEngine;
    private readonly IAudioSettingsRepository _settingsRepository;
    private readonly StudioContext            _studioContext;

    public AuxController(
        IAudioEngine             audioEngine,
        IAudioSettingsRepository settingsRepository,
        StudioContext            studioContext)
    {
        _audioEngine        = audioEngine;
        _settingsRepository = settingsRepository;
        _studioContext      = studioContext;
    }

    private bool ValidIndex(int index) => index is >= 0 and <= 7;

    private ActionResult IndexError()
        => BadRequest(new ErrorResponseDto("BAD_REQUEST", "Aux index must be between 0 and 7.", HttpContext.TraceIdentifier));

    [HttpPost("{index:int}/load")]
    public async Task<ActionResult<AuxLoadedDto>> Load(int index, [FromBody] AuxLoadRequestDto body)
    {
        if (!ValidIndex(index)) return IndexError();

        var r = await _audioEngine.LoadAuxAsync(index, body.FilePath);
        return Ok(new AuxLoadedDto(r.DurationMs, r.WaveformPath, r.StartMs, r.EndMs));
    }

    [HttpPost("{index:int}/play")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> Play(int index)
    {
        if (!ValidIndex(index)) return IndexError();
        await _audioEngine.PlayAuxAsync(index);
        return Ok(new PlaybackStatusResponseDto("OK", "ACTIVE"));
    }

    [HttpPost("{index:int}/pause")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> Pause(int index)
    {
        if (!ValidIndex(index)) return IndexError();
        await _audioEngine.PauseAuxAsync(index);
        return Ok(new PlaybackStatusResponseDto("OK", "PAUSED"));
    }

    [HttpPost("{index:int}/stop")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> Stop(int index)
    {
        if (!ValidIndex(index)) return IndexError();
        var settings = await _settingsRepository.GetByStudioAsync(_studioContext.StudioId);
        await _audioEngine.StopAuxAsync(index, settings?.AuxFadeoutMs ?? 0);
        return Ok(new PlaybackStatusResponseDto("OK", "IDLE"));
    }

    [HttpPost("{index:int}/eject")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> Eject(int index)
    {
        if (!ValidIndex(index)) return IndexError();
        await _audioEngine.EjectAuxAsync(index);
        return Ok(new PlaybackStatusResponseDto("OK", "IDLE"));
    }

    [HttpPost("{index:int}/loop")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> Loop(int index, [FromBody] AuxLoopRequestDto body)
    {
        if (!ValidIndex(index)) return IndexError();
        await _audioEngine.SetAuxLoopAsync(index, body.Enabled);
        return Ok(new PlaybackStatusResponseDto("OK", body.Enabled ? "LOOP_ON" : "LOOP_OFF"));
    }

    [HttpPost("{index:int}/volume")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> Volume(int index, [FromBody] AuxVolumeRequestDto body)
    {
        if (!ValidIndex(index)) return IndexError();
        await _audioEngine.SetAuxVolumeAsync(index, body.Gain);
        return Ok(new PlaybackStatusResponseDto("OK", "ACTIVE"));
    }

    [HttpPost("{index:int}/route")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> Route(int index, [FromBody] AuxRouteRequestDto body)
    {
        if (!ValidIndex(index)) return IndexError();
        await _audioEngine.SetAuxRouteAsync(index, body.On, body.Pfl);
        return Ok(new PlaybackStatusResponseDto("OK", "ACTIVE"));
    }
}
