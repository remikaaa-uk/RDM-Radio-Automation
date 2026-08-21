namespace RDM.Infrastructure.Database;

public sealed class Migration_35_0_0_DropInputLineFade : IMigration
{
    public string Version     => "35.0.0";
    public string Description => "Drop input_line_fadein_ms / input_line_fadeout_ms — dead line-in fade fields, never read by the audio engine";

    public string UpSql => """
        ALTER TABLE audio_settings
            DROP COLUMN IF EXISTS input_line_fadein_ms,
            DROP COLUMN IF EXISTS input_line_fadeout_ms;
        """;
}
