using RDM.Shared.Enums;

namespace RDM.Core.Models;

public record AssetSearchParams(
    string?      Query,
    AssetType?   AssetType,
    string?      FormatId,
    AssetStatus? Status,
    int          Limit,
    int          Offset,
    string?      Genre          = null,
    string?      SubcategoryId  = null,
    string?      SortColumn     = null,
    bool         SortAscending  = true
);
