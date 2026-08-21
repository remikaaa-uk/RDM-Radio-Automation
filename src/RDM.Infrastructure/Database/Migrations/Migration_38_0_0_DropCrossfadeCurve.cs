namespace RDM.Infrastructure.Database;

public sealed class Migration_38_0_0_DropCrossfadeCurve : IMigration
{
    public string Version     => "38.0.0";
    public string Description => "Drop crossfade_curve — the crossfade fade-out is now always equal-power (BassAudioEngine.StartEqualPowerFadeOut); the setting was never read";

    public string UpSql => """
        ALTER TABLE audio_settings
            DROP COLUMN IF EXISTS crossfade_curve;
        """;
}
