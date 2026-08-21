namespace RDM.Infrastructure.Database;

public sealed class Migration_22_0_0_AddFeedbackMappings : IMigration
{
    public string Version     => "22.0.0";
    public string Description => "Hardware feedback system — feedback_mappings table (LED/GPO output rules)";

    public string UpSql => """
        CREATE TABLE IF NOT EXISTS feedback_mappings (
            id                  CHAR(36)            NOT NULL,
            event_name          VARCHAR(100)        NOT NULL,
            target_device_id    VARCHAR(255)        NOT NULL,
            target_device_type  VARCHAR(50)         NOT NULL,
            channel             TINYINT UNSIGNED    NOT NULL DEFAULT 1,
            note_code           TINYINT UNSIGNED    NOT NULL,
            velocity            TINYINT UNSIGNED    NOT NULL DEFAULT 127,
            is_enabled          TINYINT(1)          NOT NULL DEFAULT 1,
            PRIMARY KEY (id),
            INDEX idx_fm_event (event_name)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;
}
