using Microsoft.AspNetCore.Mvc;
using RDM.API.Services;
using RDM.Shared.DTOs;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/assets")]
public sealed class ScanController : ControllerBase
{
    private readonly ScanJobService _scans;

    public ScanController(ScanJobService scans)
    {
        _scans = scans;
    }

    [HttpPost("scan")]
    public IActionResult StartScan([FromBody] ScanRequestDto request)
    {
        if (request?.FilePaths is null || request.FilePaths.Count == 0)
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "FilePaths cannot be empty.", HttpContext.TraceIdentifier));

        var scanId = _scans.Enqueue(request.FilePaths);
        return Accepted(new ScanResponseDto(scanId, "QUEUED"));
    }

    [HttpGet("scan/{scanId}")]
    public IActionResult GetScanStatus(string scanId)
    {
        var job = _scans.GetJob(scanId);
        if (job is null)
            return NotFound(new ErrorResponseDto("NOT_FOUND", $"Scan job {scanId} not found.", HttpContext.TraceIdentifier));

        return Ok(new ScanStatusDto(job.ScanId, job.Status, job.Done, job.Total, job.CompletedAt));
    }

    [HttpGet("scan/{scanId}/results")]
    public IActionResult GetScanResults(string scanId)
    {
        var job = _scans.GetJob(scanId);
        if (job is null)
            return NotFound(new ErrorResponseDto("NOT_FOUND", $"Scan job {scanId} not found.", HttpContext.TraceIdentifier));

        if (job.Status != "COMPLETED")
            return Conflict(new ErrorResponseDto("SCAN_NOT_COMPLETED", $"Scan job {scanId} has not completed (status: {job.Status}).", HttpContext.TraceIdentifier));

        return Ok(job.Results);
    }
}
