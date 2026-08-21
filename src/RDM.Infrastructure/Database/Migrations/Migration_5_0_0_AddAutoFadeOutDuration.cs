namespace RDM.Infrastructure.Database;

public sealed class Migration_5_0_0_AddAutoFadeOutDuration : IMigration
{
    public string Version     => "5.0.0";
    public string Description => "Add auto_fade_out_duration_s to audio_settings";

    public string UpSql => """
        ALTER TABLE audio_settings
            ADD COLUMN IF NOT EXISTS auto_fade_out_duration_s DOUBLE NOT NULL DEFAULT 10;
        """;
}
