using Microsoft.AspNetCore.Mvc;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Queues;
using RDM.Shared.DTOs;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/assets/{id}/waveform")]
public sealed class WaveformController : ControllerBase
{
    private readonly IWaveformRepository _waveformRepository;
    private readonly IAssetRepository    _assetRepository;
    private readonly WaveformQueue       _waveformQueue;

    public WaveformController(
        IWaveformRepository waveformRepository,
        IAssetRepository    assetRepository,
        WaveformQueue       waveformQueue)
    {
        _waveformRepository = waveformRepository;
        _assetRepository    = assetRepository;
        _waveformQueue      = waveformQueue;
    }

    /// <summary>
    /// Returns the compressed waveform data for an asset (GZip + byte-quantized).
    /// 404 if waveform has not been generated yet — call POST /request to enqueue generation.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetWaveform(string id)
    {
        var data = await _waveformRepository.GetAsync(id, HttpContext.RequestAborted);
        if (data is null)
            return NotFound(new ErrorResponseDto(
                "WAVEFORM_NOT_FOUND",
                "Waveform has not been generated for this asset yet.",
                HttpContext.TraceIdentifier));

        return File(data, "application/octet-stream");
    }

    /// <summary>
    /// Enqueues waveform generation for an asset.
    /// 422 if the audio file is not accessible on this server instance.
    /// </summary>
    [HttpPost("request")]
    public async Task<IActionResult> RequestWaveform(string id)
    {
        var asset = await _assetRepository.GetByIdAsync(id, HttpContext.RequestAborted);
        if (asset is null)
            return NotFound(new ErrorResponseDto(
                "ASSET_NOT_FOUND",
                "Asset with given ID does not exist.",
                HttpContext.TraceIdentifier));

        if (string.IsNullOrEmpty(asset.RdmFilePath) || !System.IO.File.Exists(asset.RdmFilePath))
            return UnprocessableEntity(new ErrorResponseDto(
                "FILE_NOT_ACCESSIBLE",
                "Audio file is not accessible on this server.",
                HttpContext.TraceIdentifier));

        await _waveformQueue.Writer.WriteAsync(
            new WaveformTask(id, asset.RdmFilePath), HttpContext.RequestAborted);

        return Accepted(new { asset_id = id, message = "Waveform generation queued." });
    }
}
