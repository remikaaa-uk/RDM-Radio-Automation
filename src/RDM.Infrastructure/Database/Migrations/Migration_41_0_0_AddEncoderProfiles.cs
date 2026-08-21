namespace RDM.Infrastructure.Database;

public sealed class Migration_41_0_0_AddEncoderProfiles : IMigration
{
    public string Version     => "41.0.0";
    public string Description => "Add encoder_profiles table (SHOUTcast/Icecast streaming targets)";

    public string UpSql => """
        CREATE TABLE IF NOT EXISTS encoder_profiles (
            profile_id         CHAR(36)          NOT NULL,
            studio_id          CHAR(36)          NOT NULL,
            name               VARCHAR(255)      NOT NULL,

            format             VARCHAR(16)       NOT NULL,
            bitrate_kbps       SMALLINT UNSIGNED NOT NULL DEFAULT 128,
            sample_rate_hz     INT UNSIGNED      NULL,
            channels           TINYINT UNSIGNED  NOT NULL DEFAULT 2,

            server_type        VARCHAR(16)       NOT NULL,
            host               VARCHAR(255)      NOT NULL,
            port               SMALLINT UNSIGNED NOT NULL,
            mount              VARCHAR(255)      NULL,
            password_encrypted VARBINARY(2048)   NULL,
            use_ssl            TINYINT(1)        NOT NULL DEFAULT 0,

            stream_name        VARCHAR(255)      NULL,
            genre              VARCHAR(255)      NULL,
            stream_url         VARCHAR(500)      NULL,
            description        VARCHAR(500)      NULL,
            is_public          TINYINT(1)        NOT NULL DEFAULT 0,

            enabled            TINYINT(1)        NOT NULL DEFAULT 1,
            auto_start         TINYINT(1)        NOT NULL DEFAULT 0,

            created_at         DATETIME          NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at         DATETIME          NULL,

            PRIMARY KEY (profile_id),
            CONSTRAINT fk_encoder_profiles_studio
                FOREIGN KEY (studio_id) REFERENCES studios(studio_id) ON DELETE CASCADE,
            INDEX idx_encoder_profiles_studio (studio_id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;
}
