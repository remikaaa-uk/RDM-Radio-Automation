namespace RDM.Infrastructure.Database;

public sealed class Migration_13_0_0_DropWaveformColumns : IMigration
{
    public string Version     => "13.0.0";
    public string Description => "Drop legacy waveform_cached and waveform_path columns from assets";

    public string UpSql => """
        ALTER TABLE assets
            DROP COLUMN waveform_cached,
            DROP COLUMN waveform_path;
        """;
}
