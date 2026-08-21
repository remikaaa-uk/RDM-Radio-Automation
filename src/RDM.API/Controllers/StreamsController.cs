using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RDM.Core;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/streams")]
public sealed class StreamsController : ControllerBase
{
    private readonly IAssetRepository _assets;

    public StreamsController(IAssetRepository assets) => _assets = assets;

    [HttpPost]
    public async Task<IActionResult> AddStream(
        [FromBody] AddStreamRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Name is required.", HttpContext.TraceIdentifier));

        if (string.IsNullOrWhiteSpace(request.StreamUrl)
            || (!request.StreamUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !request.StreamUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "StreamUrl must start with http:// or https://.", HttpContext.TraceIdentifier));

        var now      = DateTime.UtcNow;
        var assetId  = Guid.NewGuid().ToString();
        var checksum = ComputeUrlChecksum(request.StreamUrl);

        var asset = new Asset
        {
            AssetId     = assetId,
            AssetType   = AssetType.InternetStream,
            Title       = request.Name.Trim(),
            StreamUrl   = request.StreamUrl.Trim(),
            Checksum    = checksum,
            DurationMs  = 0,
            Status      = AssetStatus.Active,
            IsDamaged   = false,
            PlayCount   = 0,
            CreatedAt   = now,
            UpdatedAt   = now
        };

        try
        {
            await _assets.CreateAsync(asset, ct);
        }
        catch (DuplicateAssetException)
        {
            var existing = await _assets.GetByChecksumAsync(checksum, ct);
            if (existing is not null)
                return Ok(new AddStreamResponseDto(existing.AssetId, existing.Title, existing.StreamUrl ?? request.StreamUrl.Trim()));
            return Conflict(new ErrorResponseDto("DUPLICATE", "A stream with this URL already exists.", HttpContext.TraceIdentifier));
        }

        return Created(
            $"/api/v1/assets/{assetId}",
            new AddStreamResponseDto(assetId, asset.Title, asset.StreamUrl));
    }

    private static string ComputeUrlChecksum(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim().ToLowerInvariant()));
        return "stream:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
