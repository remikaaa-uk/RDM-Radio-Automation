namespace RDM.Infrastructure.Database;

public sealed class Migration_18_0_0_AddStreamUrl : IMigration
{
    public string Version     => "18.0.0";
    public string Description => "Add stream_url column to assets for InternetStream type";

    public string UpSql => """
        ALTER TABLE assets
            ADD COLUMN IF NOT EXISTS stream_url TEXT NULL;
        """;
}
