namespace RDM.Infrastructure.Database;

public sealed class Migration_23_0_0_AddDrMixerFeedbackFields : IMigration
{
    public string Version     => "23.0.0";
    public string Description => "Add D&R mixer fields to feedback_mappings (dr_target, dr_gpo_index)";

    public string UpSql => """
        ALTER TABLE feedback_mappings
            ADD COLUMN dr_target    VARCHAR(50) NULL AFTER velocity,
            ADD COLUMN dr_gpo_index INT         NULL AFTER dr_target;
        """;
}
