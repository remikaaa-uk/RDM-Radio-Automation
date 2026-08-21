namespace RDM.Infrastructure.Database;

public sealed class Migration_3_6_0_AddUsers : IMigration
{
    public string Version => "3.6.0";
    public string Description => "Add users table (Administrator/Operator, bcrypt)";

    public string UpSql => """
        CREATE TABLE IF NOT EXISTS users (
            user_id       CHAR(36)        NOT NULL,
            studio_id     CHAR(36)        NOT NULL,
            username      VARCHAR(64)     NOT NULL,
            password_hash VARCHAR(255)    NOT NULL,
            role          ENUM('ADMINISTRATOR','OPERATOR') NOT NULL DEFAULT 'OPERATOR',
            enabled       TINYINT(1)      NOT NULL DEFAULT 1,
            created_at    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
            last_login_at DATETIME        NULL,
            PRIMARY KEY (user_id),
            UNIQUE KEY uq_username (studio_id, username),
            CONSTRAINT fk_users_studio
                FOREIGN KEY (studio_id) REFERENCES studios(studio_id) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;
}
