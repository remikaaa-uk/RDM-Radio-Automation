using System.Collections.Generic;

namespace RDM.Shared.DTOs;

public record AssetFormatDto(
    string  FormatId,
    string  Name,
    string? Description
);

public record AssetFormatsEnvelopeDto(IReadOnlyList<AssetFormatDto> Items);
