namespace RDM.Infrastructure.Database;

public sealed class Migration_7_0_0_AddPlaylistType : IMigration
{
    public string Version     => "7.0.0";
    public string Description => "Add playlist_type column to playlists — separates on-air queue from saved playlists";

    public string UpSql => """
        ALTER TABLE playlists
            ADD COLUMN playlist_type ENUM('ON_AIR','SAVED') NOT NULL DEFAULT 'SAVED';
        """;
}
