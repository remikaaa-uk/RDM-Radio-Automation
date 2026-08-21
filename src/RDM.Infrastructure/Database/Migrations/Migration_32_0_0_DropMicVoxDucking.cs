namespace RDM.Infrastructure.Database;

public sealed class Migration_32_0_0_DropMicVoxDucking : IMigration
{
    public string Version     => "32.0.0";
    public string Description => "Drop VOX (voice-activated) mic ducking settings; ducking is now triggered by the mic button and uses ducking_level_db / ducking_attack_ms / ducking_release_ms";

    public string UpSql => """
        ALTER TABLE audio_settings
            DROP COLUMN IF EXISTS mic_ducking_enabled,
            DROP COLUMN IF EXISTS mic_duck_threshold_db,
            DROP COLUMN IF EXISTS mic_duck_attack_confirm_ms,
            DROP COLUMN IF EXISTS mic_duck_release_hold_ms,
            DROP COLUMN IF EXISTS mic_duck_filter_low_cut,
            DROP COLUMN IF EXISTS mic_duck_filter_high_cut;
        """;
}
