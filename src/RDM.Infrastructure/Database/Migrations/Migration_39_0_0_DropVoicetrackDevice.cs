namespace RDM.Infrastructure.Database;

public sealed class Migration_39_0_0_DropVoicetrackDevice : IMigration
{
    public string Version     => "39.0.0";
    public string Description => "Drop device_voicetrack_id — voice-track recording uses the mic input device; this output-device setting had no UI control and was never read";

    // Drop the FK (fk_settings_voicetrack, named in the initial schema) before the column.
    public string UpSql => """
        ALTER TABLE audio_settings DROP FOREIGN KEY IF EXISTS fk_settings_voicetrack;
        ALTER TABLE audio_settings DROP COLUMN IF EXISTS device_voicetrack_id;
        """;
}
