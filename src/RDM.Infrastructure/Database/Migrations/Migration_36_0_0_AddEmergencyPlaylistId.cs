namespace RDM.Infrastructure.Database;

public sealed class Migration_36_0_0_AddEmergencyPlaylistId : IMigration
{
    public string Version     => "36.0.0";
    public string Description => "Add emergency_playlist_id to audio_settings (dead-air recovery playlist loaded in AUTO mode)";

    public string UpSql => """
        ALTER TABLE audio_settings
            ADD COLUMN IF NOT EXISTS emergency_playlist_id CHAR(36) NULL;
        """;
}
