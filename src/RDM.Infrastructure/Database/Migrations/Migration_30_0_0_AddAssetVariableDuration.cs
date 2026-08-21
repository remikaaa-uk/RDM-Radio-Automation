namespace RDM.Infrastructure.Database;

public sealed class Migration_30_0_0_AddAssetVariableDuration : IMigration
{
    public string Version     => "30.0.0";
    public string Description => "Add is_variable_duration flag to assets (live cue re-analysis at load for content like news bulletins)";

    public string UpSql => """
        ALTER TABLE assets
            ADD COLUMN IF NOT EXISTS is_variable_duration TINYINT(1) NOT NULL DEFAULT 0;
        """;
}
