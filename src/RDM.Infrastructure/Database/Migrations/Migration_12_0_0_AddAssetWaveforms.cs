namespace RDM.Infrastructure.Database;

public sealed class Migration_12_0_0_AddAssetWaveforms : IMigration
{
    public string Version     => "12.0.0";
    public string Description => "Add asset_waveforms table for DB-stored waveform cache";

    public string UpSql => """
        CREATE TABLE IF NOT EXISTS asset_waveforms (
            asset_id   VARCHAR(36)  NOT NULL,
            wave_data  MEDIUMBLOB   NOT NULL,
            n_points   INT          NOT NULL,
            version    TINYINT      NOT NULL DEFAULT 2,
            created_at DATETIME     NOT NULL,
            PRIMARY KEY (asset_id),
            CONSTRAINT fk_aw_asset
                FOREIGN KEY (asset_id) REFERENCES assets(asset_id) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;
}
