namespace RDM.Infrastructure.Database;

public sealed class Migration_40_0_0_AddAuxFadeoutMs : IMigration
{
    public string Version     => "40.0.0";
    public string Description => "Add aux_fadeout_ms to audio_settings (fade-out duration for AUX deck stop)";

    public string UpSql => """
        ALTER TABLE audio_settings
            ADD COLUMN IF NOT EXISTS aux_fadeout_ms INT UNSIGNED NOT NULL DEFAULT 0;
        """;
}
