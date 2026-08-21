using Microsoft.AspNetCore.Mvc;
using RDM.Core.Interfaces;
using RDM.Shared.DTOs;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.API.Controllers;

[ApiController]
[Route("api/v1/categories")]
public sealed class CategoriesController : ControllerBase
{
    private readonly IAssetFormatRepository  _formatRepo;
    private readonly ISubcategoryRepository  _subcategoryRepo;
    private readonly IGenreRepository        _genreRepo;

    public CategoriesController(
        IAssetFormatRepository  formatRepo,
        ISubcategoryRepository  subcategoryRepo,
        IGenreRepository        genreRepo)
    {
        _formatRepo      = formatRepo;
        _subcategoryRepo = subcategoryRepo;
        _genreRepo       = genreRepo;
    }

    // ── Categories ────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CategoryDto>>> GetAll()
    {
        var items = await _formatRepo.GetAllAsync();
        return Ok(items.Select(f => new CategoryDto(f.FormatId, f.Name, f.Description)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<CategoryResponseDto>> Create([FromBody] CreateCategoryRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Name is required.", HttpContext.TraceIdentifier));

        var created = await _formatRepo.CreateAsync(request.Name.Trim());
        return CreatedAtAction(nameof(GetAll), new CategoryResponseDto(created.FormatId, created.Name));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<CategoryResponseDto>> Rename(string id, [FromBody] RenameCategoryRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Name is required.", HttpContext.TraceIdentifier));

        var existing = await _formatRepo.GetByIdAsync(id);
        if (existing is null)
            return NotFound(new ErrorResponseDto("NOT_FOUND", "Category not found.", HttpContext.TraceIdentifier));

        await _formatRepo.RenameAsync(id, request.Name.Trim());
        return Ok(new CategoryResponseDto(id, request.Name.Trim()));
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<DeleteCategoryResponseDto>> Delete(string id)
    {
        var existing = await _formatRepo.GetByIdAsync(id);
        if (existing is null)
            return NotFound(new ErrorResponseDto("NOT_FOUND", "Category not found.", HttpContext.TraceIdentifier));

        var count = await _formatRepo.CountAssetsAsync(id);
        if (count > 0)
            return Ok(new DeleteCategoryResponseDto(id, false, $"Category has {count} assigned track(s). Reassign them first."));

        await _formatRepo.DeleteAsync(id);
        return Ok(new DeleteCategoryResponseDto(id, true, null));
    }

    // ── Subcategories ─────────────────────────────────────────────────────────

    [HttpGet("{id}/subcategories")]
    public async Task<ActionResult<SubcategoriesEnvelopeDto>> GetSubcategories(string id)
    {
        var items = await _subcategoryRepo.GetByFormatIdAsync(id);
        var dtos  = items.Select(s => new SubcategoryDto(s.SubcategoryId, s.FormatId, s.Name, s.SortOrder)).ToList();
        return Ok(new SubcategoriesEnvelopeDto(id, dtos));
    }

    [HttpPost("{id}/subcategories")]
    public async Task<ActionResult<SubcategoryResponseDto>> CreateSubcategory(
        string id, [FromBody] CreateSubcategoryRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Name is required.", HttpContext.TraceIdentifier));

        var existing = await _formatRepo.GetByIdAsync(id);
        if (existing is null)
            return NotFound(new ErrorResponseDto("NOT_FOUND", "Category not found.", HttpContext.TraceIdentifier));

        var created = await _subcategoryRepo.CreateAsync(id, request.Name.Trim());
        return CreatedAtAction(nameof(GetSubcategories), new { id },
            new SubcategoryResponseDto(created.SubcategoryId, created.FormatId, created.Name));
    }

    [HttpPut("subcategories/{subcategoryId}")]
    public async Task<ActionResult<SubcategoryResponseDto>> RenameSubcategory(
        string subcategoryId, [FromBody] RenameSubcategoryRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Name is required.", HttpContext.TraceIdentifier));

        await _subcategoryRepo.RenameAsync(subcategoryId, request.Name.Trim());
        return Ok(new SubcategoryResponseDto(subcategoryId, string.Empty, request.Name.Trim()));
    }

    [HttpDelete("subcategories/{subcategoryId}")]
    public async Task<ActionResult<DeleteSubcategoryResponseDto>> DeleteSubcategory(string subcategoryId)
    {
        var count = await _subcategoryRepo.CountAssetsAsync(subcategoryId);
        if (count > 0)
            return Ok(new DeleteSubcategoryResponseDto(subcategoryId, false,
                $"Subcategory has {count} assigned track(s). Reassign them first."));

        await _subcategoryRepo.DeleteAsync(subcategoryId);
        return Ok(new DeleteSubcategoryResponseDto(subcategoryId, true, null));
    }

    // ── Genres ────────────────────────────────────────────────────────────────

    [HttpGet("genres")]
    public async Task<ActionResult<GenresEnvelopeDto>> GetGenres()
    {
        var items = await _genreRepo.GetAllAsync();
        var dtos  = items.Select(g => new GenreDto(g.GenreId, g.Name, g.SortOrder)).ToList();
        return Ok(new GenresEnvelopeDto(dtos));
    }

    [HttpPost("genres")]
    public async Task<ActionResult<GenreResponseDto>> CreateGenre([FromBody] CreateGenreRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Name is required.", HttpContext.TraceIdentifier));

        var created = await _genreRepo.CreateAsync(request.Name.Trim());
        return CreatedAtAction(nameof(GetGenres), new GenreResponseDto(created.GenreId, created.Name));
    }

    [HttpPut("genres/{genreId}")]
    public async Task<ActionResult<GenreResponseDto>> RenameGenre(
        string genreId, [FromBody] RenameGenreRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new ErrorResponseDto("BAD_REQUEST", "Name is required.", HttpContext.TraceIdentifier));

        await _genreRepo.RenameAsync(genreId, request.Name.Trim());
        return Ok(new GenreResponseDto(genreId, request.Name.Trim()));
    }

    [HttpDelete("genres/{genreId}")]
    public async Task<ActionResult> DeleteGenre(string genreId)
    {
        await _genreRepo.DeleteAsync(genreId);
        return Ok();
    }
}
