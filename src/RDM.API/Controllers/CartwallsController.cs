using Microsoft.AspNetCore.Mvc;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.DTOs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class CartwallsController : ControllerBase
{
    private readonly ICartwallRepository       _cartwallRepository;
    private readonly IAssetRepository          _assetRepository;
    private readonly IAudioEngine              _audioEngine;
    private readonly IAudioSettingsRepository  _settingsRepository;
    private readonly StudioContext             _studioContext;

    public CartwallsController(
        ICartwallRepository      cartwallRepository,
        IAssetRepository         assetRepository,
        IAudioEngine             audioEngine,
        IAudioSettingsRepository settingsRepository,
        StudioContext            studioContext)
    {
        _cartwallRepository = cartwallRepository;
        _assetRepository    = assetRepository;
        _audioEngine        = audioEngine;
        _settingsRepository = settingsRepository;
        _studioContext      = studioContext;
    }

    [HttpPost("cartwalls")]
    public async Task<ActionResult<CartwallDto>> CreateCartwall([FromBody] CreateCartwallDto request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Name is required.", HttpContext.TraceIdentifier));

        var existing = await _cartwallRepository.GetByStudioAsync(_studioContext.StudioId);
        var nextOrder = existing.Count > 0 ? existing.Max(c => c.PageOrder) + 1 : 0;

        var cartwall = new Cartwall
        {
            CartwallId = Guid.NewGuid().ToString(),
            StudioId   = _studioContext.StudioId,
            Name       = request.Name.Trim(),
            PageOrder  = (byte)Math.Min(nextOrder, 255)
        };
        await _cartwallRepository.CreateAsync(cartwall);

        var audioSettings = await _settingsRepository.GetByStudioAsync(_studioContext.StudioId);
        var slotsPerPage  = audioSettings?.CartwallSlotsPerPage ?? 16;
        await _cartwallRepository.EnsureSlotsAsync(cartwall.CartwallId, slotsPerPage);

        return Ok(new CartwallDto(cartwall.CartwallId, cartwall.Name, cartwall.PageOrder, cartwall.Hotkey,
            new List<CartSlotDto>()));
    }

    [HttpDelete("cartwalls/{cartwallId}")]
    public async Task<IActionResult> DeleteCartwall(string cartwallId)
    {
        var existing = await _cartwallRepository.GetByStudioAsync(_studioContext.StudioId);
        if (existing.Count <= 1)
            return UnprocessableEntity(new ErrorResponseDto("LAST_CARTWALL", "Cannot delete the last cartwall.", HttpContext.TraceIdentifier));

        var cartwall = await _cartwallRepository.GetByIdAsync(cartwallId);
        if (cartwall is null)
            return NotFound(new ErrorResponseDto("CARTWALL_NOT_FOUND", "Cartwall not found.", HttpContext.TraceIdentifier));

        await _cartwallRepository.DeleteAsync(cartwallId);

        // Re-number remaining cartwalls to close gap in page_order
        var remaining = (await _cartwallRepository.GetByStudioAsync(_studioContext.StudioId))
            .OrderBy(c => c.PageOrder).ToList();
        for (var i = 0; i < remaining.Count; i++)
        {
            if (remaining[i].PageOrder == i) continue;
            var r = remaining[i];
            await _cartwallRepository.UpdateAsync(new Cartwall
            {
                CartwallId = r.CartwallId,
                StudioId   = r.StudioId,
                Name       = r.Name,
                PageOrder  = (byte)i,
                Hotkey     = r.Hotkey
            });
        }

        return NoContent();
    }

    [HttpPatch("cartwalls/{cartwallId}")]
    public async Task<IActionResult> PatchCartwall(string cartwallId, [FromBody] PatchCartwallDto request)
    {
        if (request is null)
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Request body is required.", HttpContext.TraceIdentifier));

        var cartwall = await _cartwallRepository.GetByIdAsync(cartwallId);
        if (cartwall is null)
            return NotFound(new ErrorResponseDto("CARTWALL_NOT_FOUND", "Cartwall not found.", HttpContext.TraceIdentifier));

        var updated = new Cartwall
        {
            CartwallId = cartwall.CartwallId,
            StudioId   = cartwall.StudioId,
            Name       = request.Name      ?? cartwall.Name,
            PageOrder  = request.PageOrder.HasValue ? (byte)Math.Clamp(request.PageOrder.Value, 0, 255) : cartwall.PageOrder,
            Hotkey     = cartwall.Hotkey
        };
        await _cartwallRepository.UpdateAsync(updated);
        return NoContent();
    }

    [HttpGet("cartwalls")]
    public async Task<ActionResult<CartwallsEnvelopeDto>> GetCartwalls()
    {
        var cartwalls = await _cartwallRepository.GetByStudioAsync(_studioContext.StudioId);

        var audioSettings = await _settingsRepository.GetByStudioAsync(_studioContext.StudioId);
        var slotsPerPage  = audioSettings?.CartwallSlotsPerPage ?? 16;

        // Self-heal: studios created before cartwall seeding (or never seeded) have no
        // cartwall row, so the tab would render empty. Create a default page on demand.
        if (cartwalls.Count == 0)
        {
            await _cartwallRepository.CreateAsync(new Cartwall
            {
                CartwallId = Guid.NewGuid().ToString(),
                StudioId   = _studioContext.StudioId,
                Name       = "Cartwall 1",
                PageOrder  = 0
            });
            cartwalls = await _cartwallRepository.GetByStudioAsync(_studioContext.StudioId);
        }

        // Ensure every cartwall has exactly slotsPerPage slot rows (adds missing, leaves extras).
        var ensureTasks = cartwalls.Select(c => _cartwallRepository.EnsureSlotsAsync(c.CartwallId, slotsPerPage));
        await Task.WhenAll(ensureTasks);

        // Load slots for all cartwalls, then denormalize asset info.
        var slotTasks = cartwalls.Select(c => _cartwallRepository.GetSlotsAsync(c.CartwallId));
        var allSlots  = await Task.WhenAll(slotTasks);

        var assetIds = allSlots
            .SelectMany(s => s)
            .Where(s => s.AssetId is not null)
            .Select(s => s.AssetId!)
            .Distinct()
            .ToList();

        var assetTasks = assetIds.Select(id => _assetRepository.GetByIdAsync(id));
        var assets     = await Task.WhenAll(assetTasks);
        var assetMap   = assets.Where(a => a is not null).ToDictionary(a => a!.AssetId, a => a!);

        var dtos = cartwalls.Zip(allSlots, (cw, slots) => new CartwallDto(
            CartwallId: cw.CartwallId,
            Name:       cw.Name,
            PageOrder:  cw.PageOrder,
            Hotkey:     cw.Hotkey,
            Slots:      slots
                .Where(s => s.SlotNumber <= slotsPerPage)
                .Select(s => MapSlot(s, assetMap))
                .ToList()
        )).ToList();

        return Ok(new CartwallsEnvelopeDto(dtos));
    }

    [HttpPost("cartwall/{slotId}/trigger")]
    public async Task<ActionResult<CartTriggerResponseDto>> TriggerSlot(string slotId)
    {
        var slot = await _cartwallRepository.GetSlotByIdAsync(slotId);
        if (slot is null)
            return NotFound(new ErrorResponseDto("SLOT_NOT_FOUND", "Cart slot with given ID does not exist.", HttpContext.TraceIdentifier));

        if (slot.AssetId is null)
            return UnprocessableEntity(new ErrorResponseDto("SLOT_EMPTY", "Cart slot has no asset assigned.", HttpContext.TraceIdentifier));

        var asset = await _assetRepository.GetByIdAsync(slot.AssetId);
        if (asset is null)
            return NotFound(new ErrorResponseDto("ASSET_NOT_FOUND", "Asset assigned to slot does not exist.", HttpContext.TraceIdentifier));

        if (asset.RdmFilePath is null)
            return UnprocessableEntity(new ErrorResponseDto("ASSET_NO_FILE", "Asset has no audio file.", HttpContext.TraceIdentifier));

        if (asset.IsDamaged)
            return UnprocessableEntity(new ErrorResponseDto("ASSET_DAMAGED", "Asset is marked as damaged and cannot be played.", HttpContext.TraceIdentifier));

        await _audioEngine.TriggerCartAsync(slotId, asset.AssetId, asset.RdmFilePath, slot.Loop);

        return Ok(new CartTriggerResponseDto(slotId, "TRIGGERED", asset.DurationMs));
    }

    [HttpPost("cartwall/{slotId}/stop")]
    public async Task<ActionResult<CartTriggerResponseDto>> StopSlot(string slotId)
    {
        var slot = await _cartwallRepository.GetSlotByIdAsync(slotId);
        if (slot is null)
            return NotFound(new ErrorResponseDto("SLOT_NOT_FOUND", "Cart slot with given ID does not exist.", HttpContext.TraceIdentifier));

        await _audioEngine.StopCartAsync(slotId);
        return Ok(new CartTriggerResponseDto(slotId, "STOPPED", null));
    }

    [HttpPatch("cartwall/{slotId}")]
    public async Task<IActionResult> PatchSlot(string slotId, [FromBody] PatchCartSlotDto request)
    {
        if (request is null)
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Request body is required.", HttpContext.TraceIdentifier));

        var slot = await _cartwallRepository.GetSlotByIdAsync(slotId);
        if (slot is null)
            return NotFound(new ErrorResponseDto("SLOT_NOT_FOUND", "Cart slot with given ID does not exist.", HttpContext.TraceIdentifier));

        var updated = new CartSlot
        {
            SlotId       = slot.SlotId,
            CartwallId   = slot.CartwallId,
            AssetId      = request.ClearAsset ? null : (request.AssetId ?? slot.AssetId),
            SlotNumber   = slot.SlotNumber,
            Label        = request.Label     ?? slot.Label,
            Color        = request.Color     ?? slot.Color,
            Hotkey       = request.Hotkey    ?? slot.Hotkey,
            Loop         = request.Loop      ?? slot.Loop,
            FadeoutMs    = slot.FadeoutMs,
            OutputGainDb = slot.OutputGainDb
        };

        await _cartwallRepository.UpsertSlotAsync(updated);
        return NoContent();
    }

    [HttpPost("cartwalls/{cartwallId}/mode")]
    public async Task<ActionResult<PlaybackStatusResponseDto>> SetMode(
        string cartwallId,
        [FromBody] SetCartwallModeRequestDto request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Mode))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Mode is required.", HttpContext.TraceIdentifier));

        var cartwall = await _cartwallRepository.GetByIdAsync(cartwallId);
        if (cartwall is null)
            return NotFound(new ErrorResponseDto("CARTWALL_NOT_FOUND", "Cartwall with given ID does not exist.", HttpContext.TraceIdentifier));

        if (!new[] { "ON", "PFL", "OFF" }.Contains(request.Mode, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Invalid mode. Allowed: ON, PFL, OFF", HttpContext.TraceIdentifier));

        await _audioEngine.SetCartwallModeAsync(request.Mode.ToUpperInvariant(), HttpContext.RequestAborted);

        return Ok(new PlaybackStatusResponseDto("OK", request.Mode.ToUpperInvariant()));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CartSlotDto MapSlot(CartSlot slot, Dictionary<string, Asset> assetMap)
    {
        Asset? asset = slot.AssetId is not null ? assetMap.GetValueOrDefault(slot.AssetId) : null;

        return new CartSlotDto(
            SlotId:      slot.SlotId,
            CartwallId:  slot.CartwallId,
            AssetId:     slot.AssetId,
            SlotNumber:  slot.SlotNumber,
            Label:       slot.Label,
            Color:       slot.Color,
            Hotkey:      slot.Hotkey,
            Loop:        slot.Loop,
            FadeoutMs:   slot.FadeoutMs,
            OutputGainDb: slot.OutputGainDb,
            Title:       asset?.Title,
            Artist:      asset?.Artist,
            DurationMs:  asset?.DurationMs
        );
    }
}
