using Microsoft.AspNetCore.Mvc;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Shared.DTOs;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/mic")]
public sealed class MicController : ControllerBase
{
    private readonly IAudioEngine _audioEngine;

    public MicController(IAudioEngine audioEngine)
    {
        _audioEngine = audioEngine;
    }

    [HttpPost("start")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> Start(CancellationToken ct)
    {
        await _audioEngine.StartMicAsync(ct);
        return Ok(new PlaybackStatusResponseDto("OK", "ACTIVE"));
    }

    [HttpPost("stop")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> Stop(CancellationToken ct)
    {
        await _audioEngine.StopMicAsync(ct);
        return Ok(new PlaybackStatusResponseDto("OK", "IDLE"));
    }

    [HttpGet("level")]
    public ActionResult<MicLevelDto> Level()
    {
        return Ok(new MicLevelDto(_audioEngine.GetMicLevelDb()));
    }

    [HttpGet("status")]
    public ActionResult<MicStatusDto> Status()
    {
        return Ok(new MicStatusDto(_audioEngine.IsMicActive));
    }

    // ── FX chain ──────────────────────────────────────────────────────────────

    [HttpGet("fx")]
    public ActionResult<IEnumerable<MicFxDto>> GetFx()
    {
        var list = _audioEngine.GetMicFxList()
            .Select(s => new MicFxDto(s.SlotId, s.FxType.ToString(), new Dictionary<string, float>(s.Parameters)));
        return Ok(list);
    }

    /// <summary>Ranges and defaults for an effect type, so a client can build its editor.</summary>
    [HttpGet("fx/params/{fxType}")]
    public ActionResult<IEnumerable<MicFxParamDto>> GetFxParams(string fxType)
    {
        if (!Enum.TryParse<MicFxType>(fxType, ignoreCase: true, out var parsed))
            return BadRequest(new ErrorResponseDto("InvalidFxType", $"Unknown FX type: {fxType}", string.Empty));

        var list = MicFxParams.For(parsed)
            .Select(p => new MicFxParamDto(p.Key, p.Min, p.Max, p.Default, p.Unit));
        return Ok(list);
    }

    [HttpPut("fx/{slotId:int}")]
    public async Task<IActionResult> UpdateFx(
        int slotId, [FromBody] UpdateMicFxRequestDto req, CancellationToken ct)
    {
        try
        {
            await _audioEngine.UpdateMicFxAsync(slotId, req.Parameters, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new ErrorResponseDto("FxSlotNotFound", $"No mic FX slot with id {slotId}.", string.Empty));
        }
    }

    [HttpPost("fx")]
    public async Task<ActionResult<MicFxAddedDto>> AddFx(
        [FromBody] AddMicFxRequestDto req, CancellationToken ct)
    {
        if (!Enum.TryParse<MicFxType>(req.FxType, ignoreCase: true, out var fxType))
            return BadRequest(new ErrorResponseDto("InvalidFxType", $"Unknown FX type: {req.FxType}", string.Empty));

        int slotId = await _audioEngine.AddMicFxAsync(fxType, ct);
        return Ok(new MicFxAddedDto(slotId));
    }

    [HttpDelete("fx/{slotId:int}")]
    public async Task<IActionResult> RemoveFx(int slotId, CancellationToken ct)
    {
        await _audioEngine.RemoveMicFxAsync(slotId, ct);
        return NoContent();
    }

    // ── VST chain ─────────────────────────────────────────────────────────────

    [HttpGet("vst")]
    public ActionResult<IEnumerable<MicVstDto>> GetVst()
    {
        var list = _audioEngine.GetMicVstList()
            .Select(s => new MicVstDto(s.SlotId, s.PluginName, s.DllPath));
        return Ok(list);
    }

    [HttpPost("vst")]
    public async Task<ActionResult<MicVstAddedDto>> AddVst(
        [FromBody] AddMicVstRequestDto req, CancellationToken ct)
    {
        int slotId = await _audioEngine.AddMicVstAsync(req.DllPath, ct);
        return Ok(new MicVstAddedDto(slotId));
    }

    [HttpDelete("vst/{slotId:int}")]
    public async Task<IActionResult> RemoveVst(int slotId, CancellationToken ct)
    {
        await _audioEngine.RemoveMicVstAsync(slotId, ct);
        return NoContent();
    }
}
