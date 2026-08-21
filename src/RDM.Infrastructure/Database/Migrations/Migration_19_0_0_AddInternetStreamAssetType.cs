namespace RDM.Infrastructure.Database;

public sealed class Migration_19_0_0_AddInternetStreamAssetType : IMigration
{
    public string Version     => "19.0.0";
    public string Description => "Extend asset_type ENUM to include INTERNET_STREAM";

    public string UpSql => """
        ALTER TABLE assets
            MODIFY COLUMN asset_type ENUM('TRACK','CART','SWEEPER','VOICETRACK','INTERNET_STREAM')
                NOT NULL DEFAULT 'TRACK';
        """;
}
