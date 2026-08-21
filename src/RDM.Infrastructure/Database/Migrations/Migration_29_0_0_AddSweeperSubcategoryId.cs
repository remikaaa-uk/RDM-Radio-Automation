namespace RDM.Infrastructure.Database;

public sealed class Migration_29_0_0_AddSweeperSubcategoryId : IMigration
{
    public string Version     => "29.0.0";
    public string Description => "Add sweeper_subcategory_id column to audio_settings for active sweeper subcategory pool filter";

    public string UpSql => """
        ALTER TABLE audio_settings
            ADD COLUMN IF NOT EXISTS sweeper_subcategory_id CHAR(36) NULL;
        """;
}
