namespace RDM.Infrastructure.Database;

public sealed class Migration_37_0_0_DropBitDepth : IMigration
{
    public string Version     => "37.0.0";
    public string Description => "Drop bit_depth — the engine mixes float32 end-to-end and the driver negotiates the output format; the setting was never read";

    public string UpSql => """
        ALTER TABLE audio_settings
            DROP COLUMN IF EXISTS bit_depth;
        """;
}
