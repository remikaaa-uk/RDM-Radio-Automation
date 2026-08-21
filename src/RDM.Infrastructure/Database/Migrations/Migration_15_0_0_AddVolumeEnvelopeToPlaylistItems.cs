namespace RDM.Infrastructure.Database;

public sealed class Migration_15_0_0_AddVolumeEnvelopeToPlaylistItems : IMigration
{
    public string Version     => "15.0.0";
    public string Description => "Add volume_envelope JSON column to playlist_items";

    public string UpSql => """
        ALTER TABLE playlist_items
            ADD COLUMN volume_envelope TEXT NULL;
        """;
}
