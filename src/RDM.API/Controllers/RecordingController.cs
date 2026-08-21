using Microsoft.AspNetCore.Mvc;
using RDM.API.Mappers;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;
using System;
using System.Threading.Tasks;

namespace RDM.API.Controllers;

/// <summary>
/// The program-bus file recorder. One recording at a time, started and stopped by hand.
///
/// No role check: recording the station's own output is an operational act, and a presenter who
/// can put audio on air can certainly archive it. Nothing here exposes a credential.
///
/// The target folder arrives with every start rather than being held server-side — a path is
/// machine-specific while the database is shared across the studio's machines, so storing one
/// centrally would hand every machine a path that may not exist on it.
/// </summary>
[ApiController]
[Route("api/v1/recording")]
public sealed class RecordingController : ControllerBase
{
    private const int MinBitrate = 32;
    private const int MaxBitrate = 320;

    private readonly IAudioEngine _audioEngine;

    public RecordingController(IAudioEngine audioEngine) => _audioEngine = audioEngine;

    private ActionResult Error(int status, string code, string message)
        => StatusCode(status, new ErrorResponseDto(code, message, HttpContext.TraceIdentifier));

    [HttpPost("start")]
    public async Task<ActionResult<RecordingStatusDto>> Start([FromBody] RecordingStartRequestDto dto)
    {
        if (dto is null) return Error(400, "BAD_REQUEST", "Request body is required.");

        if (string.IsNullOrWhiteSpace(dto.Directory))
            return Error(400, "BAD_REQUEST", "A recording folder is required.");

        if (!Enum.TryParse<EncoderFormat>(dto.Format, ignoreCase: true, out var format))
            return Error(400, "BAD_REQUEST",
                $"Unknown format '{dto.Format}'. Supported: {string.Join(", ", Enum.GetNames<EncoderFormat>())}.");

        if (!_audioEngine.IsEncoderFormatAvailable(format))
            return Error(422, "FORMAT_UNAVAILABLE",
                $"{format} encoding is not available in this installation (missing BASSenc add-on).");

        if (dto.BitrateKbps is < MinBitrate or > MaxBitrate)
            return Error(400, "BAD_REQUEST", $"Bitrate must be between {MinBitrate} and {MaxBitrate} kbps.");

        if (dto.Channels is not (1 or 2))
            return Error(400, "BAD_REQUEST", "Channels must be 1 (mono) or 2 (stereo).");

        var status = await _audioEngine.StartRecordingAsync(new RecordingRequest(
            dto.Directory, format, dto.BitrateKbps, dto.SampleRateHz, dto.Channels, dto.NamePrefix));

        // The engine reports a refusal as state, not as an exception — an unwritable folder or a
        // format that cannot resample is the operator's problem to fix, not a server fault. 422
        // carries the engine's own message; 500 would say nothing useful.
        if (status.State == RecordingState.Error)
            return Error(422, "RECORDING_FAILED", status.Error ?? "Recording could not be started.");

        return Ok(DtoMapper.ToDto(status));
    }

    [HttpPost("stop")]
    public async Task<ActionResult<RecordingStatusDto>> Stop()
        => Ok(DtoMapper.ToDto(await _audioEngine.StopRecordingAsync()));

    [HttpGet("status")]
    public ActionResult<RecordingStatusDto> GetStatus()
        => Ok(DtoMapper.ToDto(_audioEngine.GetRecordingStatus()));
}
