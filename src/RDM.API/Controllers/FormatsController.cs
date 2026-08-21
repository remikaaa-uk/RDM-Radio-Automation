using Microsoft.AspNetCore.Mvc;
using RDM.Core.Interfaces;
using RDM.Shared.DTOs;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/formats")]
public sealed class FormatsController : ControllerBase
{
    private readonly IAssetFormatRepository _formatRepository;

    public FormatsController(IAssetFormatRepository formatRepository)
    {
        _formatRepository = formatRepository;
    }

    [HttpGet]
    public async Task<ActionResult<AssetFormatsEnvelopeDto>> GetFormats()
    {
        var formats = await _formatRepository.GetAllAsync();
        var dtos = formats.Select(f => new AssetFormatDto(f.FormatId, f.Name, f.Description)).ToList();
        return Ok(new AssetFormatsEnvelopeDto(dtos));
    }
}
