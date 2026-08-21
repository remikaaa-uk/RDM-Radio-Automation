namespace RDM.Infrastructure.Database;

public sealed class Migration_26_0_0_AddScripts : IMigration
{
    public string Version     => "26.0.0";
    public string Description => "Add scripts table for JS automation engine (Etap 6)";

    public string UpSql => """
        CREATE TABLE IF NOT EXISTS scripts (
            id          CHAR(36)      NOT NULL PRIMARY KEY,
            name        VARCHAR(200)  NOT NULL,
            language    VARCHAR(20)   NOT NULL DEFAULT 'js',
            script_body MEDIUMTEXT    NOT NULL,
            is_enabled  TINYINT(1)    NOT NULL DEFAULT 1,
            created_at  DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at  DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            INDEX idx_scripts_enabled (is_enabled)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """;
}
