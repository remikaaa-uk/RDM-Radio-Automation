namespace RDM.Infrastructure.Database;

public sealed class Migration_27_0_0_AddMusicFormatId : IMigration
{
    public string Version     => "27.0.0";
    public string Description => "Add music_format_id column to audio_settings for sweeper TRACK→TRACK gate";

    public string UpSql => """
        ALTER TABLE audio_settings
            ADD COLUMN IF NOT EXISTS music_format_id CHAR(36) NULL;
        """;
}
