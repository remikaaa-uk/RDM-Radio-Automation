namespace RDM.Infrastructure.Database;

public sealed class Migration_20_0_0_WideChecksumColumn : IMigration
{
    public string Version     => "20.0.0";
    public string Description => "Widen assets.checksum to VARCHAR(80) to accommodate stream: prefix";
    public string UpSql => """
        ALTER TABLE assets
            MODIFY COLUMN checksum VARCHAR(80) NOT NULL;
        """;
}
