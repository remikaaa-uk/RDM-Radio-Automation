namespace RDM.Infrastructure.Database;

public sealed class Migration_25_0_0_AddSerialFeedbackField : IMigration
{
    public string Version     => "25.0.0";
    public string Description => "Add serial_command to feedback_mappings for GenericSerialDriver";

    public string UpSql => """
        ALTER TABLE feedback_mappings
            ADD COLUMN serial_command VARCHAR(500) NULL
                AFTER dr_gpo_index;
        """;
}
