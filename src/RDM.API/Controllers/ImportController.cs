using Microsoft.AspNetCore.Mvc;
using RDM.API.Services;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Infrastructure.FileSystem;
using RDM.Shared.DTOs;
using System.Linq;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/assets")]
public sealed class ImportController : ControllerBase
{
    private readonly ImportJobService _jobs;
    private readonly Id3TagReader     _id3;

    public ImportController(ImportJobService jobs, IEnumerable<IMetadataReader> readers)
    {
        _jobs = jobs;
        _id3  = readers.OfType<Id3TagReader>().First();
    }

    [HttpPost("import")]
    public IActionResult StartImport([FromBody] ImportRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "FilePath is required.", HttpContext.TraceIdentifier));

        var flags = new ImportReaderFlags(
            ReadRdm:  request.ReadRdm,
            ReadMmd:  request.ReadMmd,
            ReadWfrm: request.ReadWfrm,
            ReadId3:  request.ReadId3);

        var importId = _jobs.Enqueue(request.FilePath, flags, request.FormatId, request.SubcategoryId);
        return Accepted(new ImportResponseDto(importId, "QUEUED", request.FilePath));
    }

    [HttpGet("import/{importId}")]
    public IActionResult GetImportStatus(string importId)
    {
        var status = _jobs.GetStatus(importId);
        if (status is null)
            return NotFound(new ErrorResponseDto("NOT_FOUND", $"Import job {importId} not found.", HttpContext.TraceIdentifier));

        return Ok(new ImportStatusDto(
            status.ImportId,
            status.Status,
            status.AssetId,
            status.Title,
            status.Artist,
            status.CompletedAt,
            status.IsDuplicate));
    }

    [HttpPost("id3/peek")]
    public async Task<IActionResult> PeekId3([FromBody] Id3PeekRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "FilePath is required.", HttpContext.TraceIdentifier));

        if (!System.IO.File.Exists(request.FilePath))
            return NotFound(new ErrorResponseDto("NOT_FOUND", "File not found.", HttpContext.TraceIdentifier));

        var meta = await _id3.TryReadAsync(request.FilePath, ct);
        if (meta is null)
            return UnprocessableEntity(new ErrorResponseDto("UNPROCESSABLE", "Could not read ID3 tags from file.", HttpContext.TraceIdentifier));

        var picBase64 = meta.PictureBytes is { Length: > 0 }
            ? Convert.ToBase64String(meta.PictureBytes)
            : null;

        return Ok(new Id3PeekResponseDto(
            meta.Title,
            meta.Artist,
            meta.Album,
            meta.Year,
            meta.Bpm,
            meta.Genre,
            meta.DurationMs.HasValue ? (int)meta.DurationMs.Value : null,
            picBase64,
            meta.PictureMimeType));
    }
}
