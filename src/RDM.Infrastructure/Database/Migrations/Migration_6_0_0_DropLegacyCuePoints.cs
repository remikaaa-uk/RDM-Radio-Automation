namespace RDM.Infrastructure.Database;

public sealed class Migration_6_0_0_DropLegacyCuePoints : IMigration
{
    public string Version     => "6.0.0";
    public string Description => "Drop legacy asset_cue_points table — named cue markers on assets table are the single source of truth";

    public string UpSql => """
        DROP TABLE IF EXISTS asset_cue_points;
        """;
}
