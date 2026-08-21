namespace RDM.Infrastructure.Database;

public sealed class Migration_33_0_0_DropInputFadeDownMusic : IMigration
{
    public string Version     => "33.0.0";
    public string Description => "Drop input_fade_down_music (AUX ducking checkbox) — never read by the audio engine";

    public string UpSql => """
        ALTER TABLE audio_settings
            DROP COLUMN IF EXISTS input_fade_down_music;
        """;
}
