namespace RDM.Infrastructure.Database;

public sealed class Migration_14_0_0_AddMicSettings : IMigration
{
    public string Version     => "14.0.0";
    public string Description => "Add microphone ducking parameters to audio_settings";

    public string UpSql => """
        ALTER TABLE audio_settings
            ADD COLUMN mic_ducking_enabled        TINYINT(1)    NOT NULL DEFAULT 1,
            ADD COLUMN mic_duck_threshold_db      DECIMAL(6,2)  NOT NULL DEFAULT -40.00,
            ADD COLUMN mic_duck_attack_confirm_ms INT UNSIGNED  NOT NULL DEFAULT 30,
            ADD COLUMN mic_duck_release_hold_ms   INT UNSIGNED  NOT NULL DEFAULT 400,
            ADD COLUMN mic_duck_filter_low_cut    DECIMAL(8,2)  NOT NULL DEFAULT 100.00,
            ADD COLUMN mic_duck_filter_high_cut   DECIMAL(8,2)  NOT NULL DEFAULT 5000.00;
        """;
}
