namespace RDM.Infrastructure.Database;

public sealed class Migration_16_0_0_FixPlayoutLogFkOnDeleteSetNull : IMigration
{
    public string Version     => "16.0.0";
    public string Description => "Fix fk_log_asset to ON DELETE SET NULL so assets with playout history can be deleted";

    public string UpSql => """
        ALTER TABLE playout_log DROP FOREIGN KEY fk_log_asset;
        ALTER TABLE playout_log ADD CONSTRAINT fk_log_asset
            FOREIGN KEY (asset_id) REFERENCES assets(asset_id) ON DELETE SET NULL;
        """;
}
