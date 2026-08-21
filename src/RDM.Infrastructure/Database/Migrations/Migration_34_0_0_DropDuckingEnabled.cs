namespace RDM.Infrastructure.Database;

public sealed class Migration_34_0_0_DropDuckingEnabled : IMigration
{
    public string Version     => "34.0.0";
    public string Description => "Drop ducking_enabled (AUX ducking checkbox) — never read by the audio engine";

    public string UpSql => """
        ALTER TABLE audio_settings
            DROP COLUMN IF EXISTS ducking_enabled;
        """;
}
