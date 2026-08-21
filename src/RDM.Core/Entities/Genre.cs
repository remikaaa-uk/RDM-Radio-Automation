namespace RDM.Core.Entities;

public sealed class Genre
{
    public string GenreId   { get; init; } = string.Empty;
    public string Name      { get; init; } = string.Empty;
    public int    SortOrder { get; init; }
    public DateTime CreatedAt { get; init; }
}
