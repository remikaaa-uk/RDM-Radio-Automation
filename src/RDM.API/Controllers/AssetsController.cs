using Microsoft.AspNetCore.Mvc;
using RDM.API.Mappers;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Queues;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/assets")]
public sealed class AssetsController : ControllerBase
{
    private readonly IAssetRepository _assetRepository;
    private readonly LoudnessQueue _loudnessQueue;
    private readonly CueAnalysisQueue _cueAnalysisQueue;
    private readonly IRdmFileWriter _rdmFileWriter;

    public AssetsController(
        IAssetRepository assetRepository,
        LoudnessQueue loudnessQueue,
        CueAnalysisQueue cueAnalysisQueue,
        IRdmFileWriter rdmFileWriter)
    {
        _assetRepository  = assetRepository;
        _loudnessQueue    = loudnessQueue;
        _cueAnalysisQueue = cueAnalysisQueue;
        _rdmFileWriter    = rdmFileWriter;
    }

    [HttpGet]
    public async Task<ActionResult<AssetSearchEnvelopeDto>> SearchAssets(
        [FromQuery] string? q,
        [FromQuery] string? asset_type,
        [FromQuery] string? format_id,
        [FromQuery] string? status,
        [FromQuery] string? genre,
        [FromQuery] string? subcategory_id,
        [FromQuery] int     limit    = 50,
        [FromQuery] int     offset   = 0,
        [FromQuery] string? sort     = null,
        [FromQuery] string? sort_dir = null)
    {
        if (limit < 1 || limit > 1000)
        {
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Limit must be between 1 and 1000.", HttpContext.TraceIdentifier));
        }

        if (offset < 0)
        {
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Offset must be greater than or equal to 0.", HttpContext.TraceIdentifier));
        }

        AssetType? parsedAssetType = null;
        if (!string.IsNullOrEmpty(asset_type))
        {
            if (Enum.TryParse<AssetType>(asset_type, true, out var t))
            {
                parsedAssetType = t;
            }
            else
            {
                return BadRequest(new ErrorResponseDto("BAD_REQUEST", $"Invalid asset_type value. Allowed values are: {string.Join(", ", Enum.GetNames<AssetType>().Select(n => n.ToUpperInvariant()))}", HttpContext.TraceIdentifier));
            }
        }

        AssetStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(status) && !status.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            if (Enum.TryParse<AssetStatus>(status, true, out var s))
            {
                parsedStatus = s;
            }
            else
            {
                return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Invalid status value. Allowed: ACTIVE, DISABLED, PENDING_REVIEW, ALL", HttpContext.TraceIdentifier));
            }
        }

        bool sortAsc = !string.Equals(sort_dir, "desc", StringComparison.OrdinalIgnoreCase);
        var searchParams = new AssetSearchParams(q, parsedAssetType, format_id, parsedStatus, limit, offset, genre, subcategory_id,
            SortColumn: sort, SortAscending: sortAsc);
        var (items, total) = await _assetRepository.SearchAsync(searchParams);

        var dtos = new List<AssetDto>(items.Count);
        foreach (var item in items)
            dtos.Add(DtoMapper.ToDto(item));

        var envelope = new AssetSearchEnvelopeDto(total, limit, offset, dtos);
        return Ok(envelope);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<AssetDto>> GetAssetById(string id)
    {
        var asset = await _assetRepository.GetByIdAsync(id);
        if (asset == null)
        {
            return NotFound(new ErrorResponseDto("ASSET_NOT_FOUND", "Asset with given ID does not exist.", HttpContext.TraceIdentifier));
        }

        var dto = DtoMapper.ToDto(asset);
        return Ok(dto);
    }

    [HttpGet("by-path")]
    public async Task<ActionResult<AssetDto>> FindByPath([FromQuery] string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "path is required.", HttpContext.TraceIdentifier));

        var asset = await _assetRepository.FindByFilePathAsync(path, ct);
        if (asset is null) return NotFound();
        return Ok(DtoMapper.ToDto(asset));
    }

    [HttpPost("analyze-loudness")]
    public async Task<ActionResult<AnalyzeLoudnessResponseDto>> AnalyzeLoudness([FromBody] AnalyzeLoudnessRequestDto request)
    {
        if (request == null || request.AssetIds == null || request.AssetIds.Count == 0)
        {
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "AssetIds list cannot be empty.", HttpContext.TraceIdentifier));
        }

        int queuedCount = 0;
        foreach (var id in request.AssetIds)
        {
            var asset = await _assetRepository.GetByIdAsync(id);
            if (asset != null && !string.IsNullOrEmpty(asset.RdmFilePath))
            {
                await _loudnessQueue.Writer.WriteAsync(new LoudnessTask(asset.AssetId, asset.RdmFilePath));
                queuedCount++;
            }
        }

        return Accepted(new AnalyzeLoudnessResponseDto(queuedCount, $"Loudness analysis queued for {queuedCount} assets."));
    }

    [HttpPost("analyze-cue")]
    public async Task<ActionResult<AnalyzeCueResponseDto>> AnalyzeCue([FromBody] AnalyzeCueRequestDto request)
    {
        if (request == null || request.AssetIds == null || request.AssetIds.Count == 0)
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "AssetIds list cannot be empty.", HttpContext.TraceIdentifier));

        int queuedCount = 0;
        foreach (var id in request.AssetIds)
        {
            var asset = await _assetRepository.GetByIdAsync(id);
            if (asset != null && !string.IsNullOrEmpty(asset.RdmFilePath))
            {
                await _cueAnalysisQueue.Writer.WriteAsync(
                    new Core.Models.CueAnalysisTask(asset.AssetId, asset.RdmFilePath,
                        request.StartDb, request.NextStartDb, request.EndDb));
                queuedCount++;
            }
        }

        return Accepted(new AnalyzeCueResponseDto(queuedCount, $"Cue analysis queued for {queuedCount} assets."));
    }

    [HttpGet("/api/v1/track/{id}")]
    public async Task<ActionResult<AssetDetailDto>> GetTrackDetail(string id)
    {
        var asset = await _assetRepository.GetByIdAsync(id);
        if (asset == null)
        {
            return NotFound(new ErrorResponseDto("ASSET_NOT_FOUND", "Asset with given ID does not exist.", HttpContext.TraceIdentifier));
        }

        var dto = DtoMapper.ToDetailDto(asset);
        return Ok(dto);
    }

    [HttpPut("/api/v1/track/{id}")]
    public async Task<ActionResult<UpdateAssetResponseDto>> UpdateAsset(string id, [FromBody] UpdateAssetRequestDto request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Title is required.", HttpContext.TraceIdentifier));

        var existing = await _assetRepository.GetByIdAsync(id);
        if (existing is null)
            return NotFound(new ErrorResponseDto("ASSET_NOT_FOUND", "Asset with given ID does not exist.", HttpContext.TraceIdentifier));

        var parsedStatus = request.Status switch
        {
            "ACTIVE"         => (AssetStatus?)AssetStatus.Active,
            "DISABLED"       => AssetStatus.Disabled,
            "PENDING_REVIEW" => AssetStatus.PendingReview,
            _                => null
        };
        if (parsedStatus is null)
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Invalid status value. Allowed: ACTIVE, DISABLED, PENDING_REVIEW", HttpContext.TraceIdentifier));

        AssetType resolvedAssetType = existing.AssetType;
        if (!string.IsNullOrEmpty(request.AssetType))
        {
            if (!Enum.TryParse<AssetType>(request.AssetType, true, out var parsedType)
                || (parsedType != AssetType.Track && parsedType != AssetType.Sweeper && parsedType != AssetType.InternetStream))
                return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Invalid asset_type. Allowed: Track, Sweeper, InternetStream.", HttpContext.TraceIdentifier));
            resolvedAssetType = parsedType;
        }

        var updatedAt = DateTime.UtcNow;
        var updated = new Asset
        {
            AssetId        = existing.AssetId,
            AssetType      = resolvedAssetType,
            FormatId       = request.FormatId      ?? existing.FormatId,
            SubcategoryId  = request.SubcategoryId ?? existing.SubcategoryId,
            Title          = request.Title,
            Artist         = request.Artist,
            Album          = request.Album,
            DurationMs     = existing.DurationMs,
            Checksum       = existing.Checksum,
            RdmFilePath    = existing.RdmFilePath,
            StreamUrl      = request.StreamUrl  ?? existing.StreamUrl,
            ImagePath      = request.ImagePath ?? existing.ImagePath,
            Bpm            = request.Bpm,
            Year           = request.Year,
            Rating         = request.Rating,
            Mood           = request.Mood,
            Gender         = request.Gender,
            Language       = request.Language,
            Genre          = request.Genre,
            Comments       = request.Comments,
            IsDamaged      = existing.IsDamaged,
            IsVariableDuration = request.IsVariableDuration ?? existing.IsVariableDuration,
            Status         = parsedStatus!.Value,
            StartDate      = request.StartDate,
            EndDate        = request.EndDate,
            PlayLimit      = request.PlayLimit,
            PlayCount      = existing.PlayCount,
            LastPlayedAt   = existing.LastPlayedAt,
            LoudnessLufs   = existing.LoudnessLufs,
            LoudnessPeak   = existing.LoudnessPeak,
            CreatedAt      = existing.CreatedAt,
            UpdatedAt      = updatedAt,
        };

        await _assetRepository.UpdateAsync(updated);

        if (request.CueMarkers is { } cm)
            await _assetRepository.UpdateCueMarkersAsync(id, cm);

        var refreshed = await _assetRepository.GetByIdAsync(id, HttpContext.RequestAborted);
        if (refreshed is not null)
        {
            try { await _rdmFileWriter.WriteAsync(refreshed, HttpContext.RequestAborted); }
            catch { /* .rdm write is non-fatal — DB is the source of truth */ }
        }

        return Ok(new UpdateAssetResponseDto(id, updatedAt));
    }

    [HttpPatch("{id}/status")]
    public async Task<ActionResult<AssetStatusUpdateResponseDto>> UpdateStatus(string id, [FromBody] AssetStatusUpdateRequestDto request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Status))
        {
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Status is required.", HttpContext.TraceIdentifier));
        }

        var parsedStatus = request.Status switch
        {
            "ACTIVE"         => (AssetStatus?)AssetStatus.Active,
            "DISABLED"       => AssetStatus.Disabled,
            "PENDING_REVIEW" => AssetStatus.PendingReview,
            _                => null
        };

        if (parsedStatus is null)
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Invalid status value. Allowed: ACTIVE, DISABLED, PENDING_REVIEW", HttpContext.TraceIdentifier));

        var asset = await _assetRepository.GetByIdAsync(id);
        if (asset == null)
            return NotFound(new ErrorResponseDto("ASSET_NOT_FOUND", "Asset with given ID does not exist.", HttpContext.TraceIdentifier));

        await _assetRepository.UpdateStatusAsync(id, parsedStatus.Value);

        return Ok(new AssetStatusUpdateResponseDto(id, request.Status, DateTime.UtcNow));
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<DeleteAssetResponseDto>> DeleteAsset(
        string id, [FromQuery] bool deleteFile = false, CancellationToken ct = default)
    {
        var asset = await _assetRepository.GetByIdAsync(id, ct);
        if (asset is null)
            return NotFound(new ErrorResponseDto("ASSET_NOT_FOUND", "Asset with given ID does not exist.", HttpContext.TraceIdentifier));

        await _assetRepository.DeleteAsync(id, ct);

        bool fileDeleted = false;
        if (deleteFile && !string.IsNullOrEmpty(asset.RdmFilePath) && System.IO.File.Exists(asset.RdmFilePath))
        {
            System.IO.File.Delete(asset.RdmFilePath);
            fileDeleted = true;
        }

        return Ok(new DeleteAssetResponseDto(id, fileDeleted));
    }

    [HttpPost("batch-delete")]
    public async Task<ActionResult<BatchDeleteAssetsResponseDto>> BatchDeleteAssets(
        [FromBody] BatchDeleteAssetsRequestDto request, CancellationToken ct = default)
    {
        if (request.AssetIds.Count == 0)
            return Ok(new BatchDeleteAssetsResponseDto(0, 0));

        int filesDeleted = 0;
        if (request.DeleteFiles)
        {
            var paths = await _assetRepository.GetFilePathsByIdsAsync(request.AssetIds, ct);
            await _assetRepository.BatchDeleteAsync(request.AssetIds, ct);
            foreach (var path in paths)
            {
                if (path is not null && System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                    filesDeleted++;
                }
            }
        }
        else
        {
            await _assetRepository.BatchDeleteAsync(request.AssetIds, ct);
        }

        return Ok(new BatchDeleteAssetsResponseDto(request.AssetIds.Count, filesDeleted));
    }

    [HttpPost("purge-orphans")]
    public async Task<ActionResult<PurgeOrphansResponseDto>> PurgeOrphans(CancellationToken ct)
    {
        var allPaths = await _assetRepository.GetAllFilePathsAsync(ct);
        var deletedTitles = new List<string>();

        foreach (var (assetId, path, title) in allPaths)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                await _assetRepository.DeleteAsync(assetId, ct);
                deletedTitles.Add(title);
            }
        }

        return Ok(new PurgeOrphansResponseDto(deletedTitles.Count, deletedTitles));
    }

    [HttpPost("optimize")]
    public async Task<ActionResult<OptimizeDatabaseResponseDto>> OptimizeDatabase(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await _assetRepository.OptimizeDatabaseAsync(ct);
        sw.Stop();
        return Ok(new OptimizeDatabaseResponseDto(true, sw.ElapsedMilliseconds));
    }
}
