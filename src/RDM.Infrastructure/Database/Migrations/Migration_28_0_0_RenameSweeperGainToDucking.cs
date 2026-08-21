namespace RDM.Infrastructure.Database;

public sealed class Migration_28_0_0_RenameSweeperGainToDucking : IMigration
{
    public string Version     => "28.0.0";
    public string Description => "Rename sweeper_gain_db → sweeper_ducking_db (track ducking while a sweeper plays, 0–12 dB)";

    public string UpSql => """
        ALTER TABLE audio_settings
            CHANGE COLUMN sweeper_gain_db sweeper_ducking_db FLOAT NOT NULL DEFAULT 6.0;
        """;
}
