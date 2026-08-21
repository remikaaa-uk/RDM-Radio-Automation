namespace RDM.Infrastructure.Database;

public sealed class Migration_11_0_0_SeedGenres : IMigration
{
    public string Version     => "11.0.0";
    public string Description => "Seed genres table with default values";

    public string UpSql => """
        INSERT IGNORE INTO genres (genre_id, name, sort_order) VALUES
            (UUID(), 'Pop',               1),
            (UUID(), 'Rock',              2),
            (UUID(), 'Dance/Electronic',  3),
            (UUID(), 'House',             4),
            (UUID(), 'Electronic',        5),
            (UUID(), 'Dance',             6),
            (UUID(), 'Hip-Hop/R&B',       7),
            (UUID(), 'R&B',               8),
            (UUID(), 'Jazz',              9),
            (UUID(), 'Classical',        10),
            (UUID(), 'Country',          11),
            (UUID(), 'Folk',             12),
            (UUID(), 'Reggae',           13),
            (UUID(), 'Ska',              14),
            (UUID(), 'Latin',            15),
            (UUID(), 'Metal',            16),
            (UUID(), 'Other',            17);
        """;
}
