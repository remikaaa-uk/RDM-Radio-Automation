namespace RDM.Infrastructure.Database;

public sealed class Migration_10_0_0_AddSubcategoriesAndGenres : IMigration
{
    public string Version     => "10.0.0";
    public string Description => "Add asset_subcategories and genres tables; add subcategory_id to assets";

    public string UpSql => """
        CREATE TABLE IF NOT EXISTS asset_subcategories (
            subcategory_id  CHAR(36)        NOT NULL,
            format_id       CHAR(36)        NOT NULL,
            name            VARCHAR(64)     NOT NULL,
            sort_order      INT             NOT NULL DEFAULT 0,
            created_at      DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (subcategory_id),
            UNIQUE KEY uq_subcat_name (format_id, name),
            CONSTRAINT fk_subcat_format
                FOREIGN KEY (format_id) REFERENCES asset_formats(format_id) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        CREATE TABLE IF NOT EXISTS genres (
            genre_id    CHAR(36)        NOT NULL,
            name        VARCHAR(64)     NOT NULL,
            sort_order  INT             NOT NULL DEFAULT 0,
            created_at  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (genre_id),
            UNIQUE KEY uq_genre_name (name)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        ALTER TABLE assets
            ADD COLUMN subcategory_id CHAR(36) NULL AFTER format_id,
            ADD CONSTRAINT fk_asset_subcategory
                FOREIGN KEY (subcategory_id) REFERENCES asset_subcategories(subcategory_id) ON DELETE SET NULL;

        CREATE INDEX IF NOT EXISTS idx_assets_subcategory ON assets (subcategory_id);
        """;
}
