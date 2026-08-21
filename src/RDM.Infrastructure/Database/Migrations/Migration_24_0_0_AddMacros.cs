namespace RDM.Infrastructure.Database;

public sealed class Migration_24_0_0_AddMacros : IMigration
{
    public string Version     => "24.0.0";
    public string Description => "Add macros and macro_steps tables";

    public string UpSql => """
        CREATE TABLE IF NOT EXISTS macros (
            id         CHAR(36)     NOT NULL PRIMARY KEY,
            name       VARCHAR(200) NOT NULL,
            is_enabled TINYINT(1)   NOT NULL DEFAULT 1,
            created_at DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        CREATE TABLE IF NOT EXISTS macro_steps (
            id                 CHAR(36)     NOT NULL PRIMARY KEY,
            macro_id           CHAR(36)     NOT NULL,
            step_order         INT          NOT NULL DEFAULT 0,
            action_id          VARCHAR(100) NULL,
            parameter          VARCHAR(500) NULL,
            output_device_type VARCHAR(50)  NULL,
            output_device_id   VARCHAR(100) NULL,
            output_command     VARCHAR(500) NULL,
            delay_after_ms     INT          NOT NULL DEFAULT 0,

            CONSTRAINT fk_macro_steps_macro
                FOREIGN KEY (macro_id) REFERENCES macros(id) ON DELETE CASCADE,
            INDEX idx_macro_steps_macro_id (macro_id),
            INDEX idx_macro_steps_order    (macro_id, step_order)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """;
}
