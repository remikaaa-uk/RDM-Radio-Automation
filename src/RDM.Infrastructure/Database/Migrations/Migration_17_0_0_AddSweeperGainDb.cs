namespace RDM.Infrastructure.Database;

public sealed class Migration_17_0_0_AddSweeperGainDb : IMigration
{
    public string Version     => "17.0.0";
    public string Description => "Add sweeper_gain_db column to audio_settings";

    public string UpSql => """
        ALTER TABLE audio_settings
            ADD COLUMN IF NOT EXISTS sweeper_gain_db FLOAT NOT NULL DEFAULT 3.0;
        """;
}
