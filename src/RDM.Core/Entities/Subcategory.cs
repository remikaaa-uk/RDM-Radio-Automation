namespace RDM.Core.Entities;

public sealed class Subcategory
{
    public string SubcategoryId { get; init; } = string.Empty;
    public string FormatId      { get; init; } = string.Empty;
    public string Name          { get; init; } = string.Empty;
    public int    SortOrder     { get; init; }
    public DateTime CreatedAt   { get; init; }
}
