using Microsoft.AspNetCore.Mvc;
using RDM.API.Mappers;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/events")]
public sealed class ScheduledEventsController : ControllerBase
{
    private readonly IScheduledEventRepository _eventRepository;
    private readonly EventScheduler _eventScheduler;
    private readonly StudioContext _studioContext;

    public ScheduledEventsController(
        IScheduledEventRepository eventRepository,
        EventScheduler eventScheduler,
        StudioContext studioContext)
    {
        _eventRepository = eventRepository;
        _eventScheduler = eventScheduler;
        _studioContext = studioContext;
    }

    private UserRole CurrentRole => HttpContext.Items["Role"] as UserRole? ?? UserRole.Operator;

    // Persist action wrapper keys as camelCase ("type"/"payload") to match the API contract
    // and what the executor/UI expect. (Default serialization emits PascalCase.)
    private static readonly JsonSerializerOptions ActionsJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Parses an optional yyyy-MM-dd date. Empty/null → null date, still valid.</summary>
    private static bool TryParseOnlyOnDate(string? value, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (DateOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
        {
            date = parsed;
            return true;
        }
        return false;
    }

    [HttpGet]
    public async Task<ActionResult<ScheduledEventResponseEnvelopeDto>> GetAllEvents()
    {
        var events = await _eventRepository.GetByStudioAsync(_studioContext.StudioId);
        var dtos = events.Select(DtoMapper.ToDto).ToList();
        return Ok(new ScheduledEventResponseEnvelopeDto(dtos));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ScheduledEventDto>> GetEventById(string id)
    {
        var ev = await _eventRepository.GetByIdAsync(id);
        if (ev == null)
        {
            return NotFound(new ErrorResponseDto("EVENT_NOT_FOUND", "Scheduled event with given ID does not exist.", HttpContext.TraceIdentifier));
        }

        return Ok(DtoMapper.ToDto(ev));
    }

    [HttpPost]
    public async Task<ActionResult<ScheduledEventCreatedResponseDto>> CreateEvent([FromBody] ScheduledEventCreateDto dto)
    {
        if (CurrentRole != UserRole.Admin)
        {
            return StatusCode(403, new ErrorResponseDto("FORBIDDEN", "Only Administrators are allowed to create scheduled events.", HttpContext.TraceIdentifier));
        }

        if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
        {
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Event name is required.", HttpContext.TraceIdentifier));
        }

        if (!Enum.TryParse<ScheduledEventType>(dto.EventType, true, out var eventType))
        {
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Invalid event type.", HttpContext.TraceIdentifier));
        }

        TimeSpan? eventHour = null;
        if (!string.IsNullOrWhiteSpace(dto.EventHour))
        {
            if (!TimeSpan.TryParse(dto.EventHour, out var eh))
            {
                return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Invalid event hour format. Use hh:mm:ss", HttpContext.TraceIdentifier));
            }
            eventHour = eh;
        }

        if (!TryParseOnlyOnDate(dto.OnlyOnDate, out var onlyOnDate))
        {
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Invalid only_on_date format. Use yyyy-MM-dd", HttpContext.TraceIdentifier));
        }

        if (eventType == ScheduledEventType.OneTime && onlyOnDate is null)
        {
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "One-time events require only_on_date (yyyy-MM-dd).", HttpContext.TraceIdentifier));
        }

        var ev = new ScheduledEvent
        {
            EventId = Guid.NewGuid().ToString(),
            StudioId = _studioContext.StudioId,
            Name = dto.Name,
            EventType = eventType,
            Category = dto.Category,
            Enabled = dto.Enabled,
            EventHour = eventHour,
            OnlyOnDate = onlyOnDate,
            Days = string.Join(",", dto.Days),
            Hours = string.Join(",", dto.Hours.Select(h => h.ToString())),
            SmartTiming = dto.SmartTiming,
            Actions = JsonSerializer.Serialize(dto.Actions, ActionsJsonOptions),
            CreatedAt = DateTime.UtcNow
        };

        await _eventRepository.CreateAsync(ev);

        var response = new ScheduledEventCreatedResponseDto(ev.EventId, ev.Name, ev.CreatedAt);
        return CreatedAtAction(nameof(GetEventById), new { id = ev.EventId }, response);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ScheduledEventUpdateResponseDto>> UpdateEvent(string id, [FromBody] ScheduledEventCreateDto dto)
    {
        if (CurrentRole != UserRole.Admin)
        {
            return StatusCode(403, new ErrorResponseDto("FORBIDDEN", "Only Administrators are allowed to update scheduled events.", HttpContext.TraceIdentifier));
        }

        var existing = await _eventRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return NotFound(new ErrorResponseDto("EVENT_NOT_FOUND", "Scheduled event with given ID does not exist.", HttpContext.TraceIdentifier));
        }

        if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
        {
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Event name is required.", HttpContext.TraceIdentifier));
        }

        if (!Enum.TryParse<ScheduledEventType>(dto.EventType, true, out var eventType))
        {
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Invalid event type.", HttpContext.TraceIdentifier));
        }

        TimeSpan? eventHour = null;
        if (!string.IsNullOrWhiteSpace(dto.EventHour))
        {
            if (!TimeSpan.TryParse(dto.EventHour, out var eh))
            {
                return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Invalid event hour format. Use hh:mm:ss", HttpContext.TraceIdentifier));
            }
            eventHour = eh;
        }

        if (!TryParseOnlyOnDate(dto.OnlyOnDate, out var onlyOnDate))
        {
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Invalid only_on_date format. Use yyyy-MM-dd", HttpContext.TraceIdentifier));
        }

        if (eventType == ScheduledEventType.OneTime && onlyOnDate is null)
        {
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "One-time events require only_on_date (yyyy-MM-dd).", HttpContext.TraceIdentifier));
        }

        var ev = new ScheduledEvent
        {
            EventId = id,
            StudioId = _studioContext.StudioId,
            Name = dto.Name,
            EventType = eventType,
            Category = dto.Category,
            Enabled = dto.Enabled,
            EventHour = eventHour,
            OnlyOnDate = onlyOnDate,
            Days = string.Join(",", dto.Days),
            Hours = string.Join(",", dto.Hours.Select(h => h.ToString())),
            SmartTiming = dto.SmartTiming,
            Actions = JsonSerializer.Serialize(dto.Actions, ActionsJsonOptions),
            LastFiredAt = existing.LastFiredAt,
            SkipNext = existing.SkipNext,
            CreatedAt = existing.CreatedAt
        };

        await _eventRepository.UpdateAsync(ev);

        return Ok(new ScheduledEventUpdateResponseDto(id, DateTime.UtcNow));
    }

    [HttpPatch("{id}")]
    public async Task<ActionResult<ScheduledEventUpdateResponseDto>> PatchEvent(string id, [FromBody] ScheduledEventPatchDto dto)
    {
        if (dto == null)
        {
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Patch body is required.", HttpContext.TraceIdentifier));
        }

        if (CurrentRole == UserRole.Operator)
        {
            if (dto.Enabled.HasValue)
            {
                return StatusCode(403, new ErrorResponseDto("FORBIDDEN", "Operators are only allowed to modify the skip_next field.", HttpContext.TraceIdentifier));
            }
        }

        var existing = await _eventRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return NotFound(new ErrorResponseDto("EVENT_NOT_FOUND", "Scheduled event with given ID does not exist.", HttpContext.TraceIdentifier));
        }

        if (dto.SkipNext.HasValue)
        {
            await _eventRepository.UpdateSkipNextAsync(id, dto.SkipNext.Value);
        }

        if (dto.Enabled.HasValue)
        {
            // Only Administrators can modify Enabled (checked in operator validation above)
            var ev = new ScheduledEvent
            {
                EventId = existing.EventId,
                StudioId = existing.StudioId,
                Name = existing.Name,
                EventType = existing.EventType,
                Category = existing.Category,
                Enabled = dto.Enabled.Value,
                EventHour = existing.EventHour,
                OnlyOnDate = existing.OnlyOnDate,
                Days = existing.Days,
                Hours = existing.Hours,
                SmartTiming = existing.SmartTiming,
                Actions = existing.Actions,
                LastFiredAt = existing.LastFiredAt,
                SkipNext = existing.SkipNext,
                CreatedAt = existing.CreatedAt
            };
            await _eventRepository.UpdateAsync(ev);
        }

        return Ok(new ScheduledEventUpdateResponseDto(id, DateTime.UtcNow));
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteEvent(string id)
    {
        if (CurrentRole != UserRole.Admin)
        {
            return StatusCode(403, new ErrorResponseDto("FORBIDDEN", "Only Administrators are allowed to delete scheduled events.", HttpContext.TraceIdentifier));
        }

        var existing = await _eventRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return NotFound(new ErrorResponseDto("EVENT_NOT_FOUND", "Scheduled event with given ID does not exist.", HttpContext.TraceIdentifier));
        }

        await _eventRepository.DeleteAsync(id);
        return NoContent();
    }

    [HttpGet("next")]
    public async Task<ActionResult<NextScheduledEventDto>> GetNextEvent()
    {
        var nextFiring = await _eventScheduler.GetNextFiringAsync();
        if (nextFiring == null)
        {
            return NoContent();
        }

        var (ev, firesAt) = nextFiring.Value;
        var remainingMs = (uint)Math.Max(0, (firesAt - DateTime.Now).TotalMilliseconds);

        var dto = new NextScheduledEventDto(
            EventId: ev.EventId,
            Name: ev.Name,
            FiresAt: firesAt,
            RemainingMs: remainingMs,
            SkipNext: ev.SkipNext
        );

        return Ok(dto);
    }
}
