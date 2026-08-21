using System.Collections.Generic;

namespace RDM.Shared.DTOs;

// ── Categories (asset_formats) ────────────────────────────────────────────────

public record CategoryDto(
    string  FormatId,
    string  Name,
    string? Description
);

public record CreateCategoryRequestDto(string Name);
public record RenameCategoryRequestDto(string Name);
public record CategoryResponseDto(string FormatId, string Name);
public record DeleteCategoryResponseDto(string FormatId, bool Deleted, string? Reason);

// ── Subcategories ─────────────────────────────────────────────────────────────

public record SubcategoryDto(
    string SubcategoryId,
    string FormatId,
    string Name,
    int    SortOrder
);

public record SubcategoriesEnvelopeDto(string FormatId, IReadOnlyList<SubcategoryDto> Items);
public record CreateSubcategoryRequestDto(string Name);
public record RenameSubcategoryRequestDto(string Name);
public record SubcategoryResponseDto(string SubcategoryId, string FormatId, string Name);
public record DeleteSubcategoryResponseDto(string SubcategoryId, bool Deleted, string? Reason);

// ── Genres ────────────────────────────────────────────────────────────────────

public record GenreDto(
    string GenreId,
    string Name,
    int    SortOrder
);

public record GenresEnvelopeDto(IReadOnlyList<GenreDto> Items);
public record CreateGenreRequestDto(string Name);
public record RenameGenreRequestDto(string Name);
public record GenreResponseDto(string GenreId, string Name);
