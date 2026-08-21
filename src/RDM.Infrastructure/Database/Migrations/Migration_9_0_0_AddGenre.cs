namespace RDM.Infrastructure.Database;

public sealed class Migration_9_0_0_AddGenre : IMigration
{
    public string Version => "9.0.0";
    public string Description => "Add genre column to assets";

    public string UpSql => """
        ALTER TABLE assets
            ADD COLUMN genre VARCHAR(64) NULL AFTER language;
        """;
}
