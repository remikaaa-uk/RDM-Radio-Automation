using Microsoft.AspNetCore.Mvc;
using RDM.API.Mappers;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.DTOs;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/events")]
public sealed class EventLogController : ControllerBase
{
    private readonly IPlayoutLogRepository _logRepository;
    private readonly StudioContext         _studioContext;

    public EventLogController(
        IPlayoutLogRepository logRepository,
        StudioContext         studioContext)
    {
        _logRepository = logRepository;
        _studioContext = studioContext;
    }

    [HttpGet("log")]
    public async Task<ActionResult<EventLogEnvelopeDto>> GetLog(
        [FromQuery] int limit = 50)
    {
        if (limit < 1 || limit > 500)
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Limit must be between 1 and 500.", HttpContext.TraceIdentifier));

        var entries = await _logRepository.GetRecentAsync(_studioContext.StudioId, limit);
        var dtos = entries.Select(e => new EventLogEntryDto(
            EventId:   e.LogId,
            EventName: string.IsNullOrEmpty(e.TempTitle) ? "(unknown)" : e.TempTitle,
            FiredAt:   e.StartedAt,
            Result:    "SUCCESS"
        )).ToList();

        return Ok(new EventLogEnvelopeDto(dtos));
    }
}
