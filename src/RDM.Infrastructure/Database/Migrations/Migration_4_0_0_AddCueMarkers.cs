namespace RDM.Infrastructure.Database;

public sealed class Migration_4_0_0_AddCueMarkers : IMigration
{
    public string Version     => "4.0.0";
    public string Description => "Add named cue marker columns to assets table";

    public string UpSql => """
        ALTER TABLE assets
            ADD COLUMN IF NOT EXISTS cue_start      DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_intro      DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_ramp2      DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_ramp3      DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_outro      DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_start_next DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_fade_out   DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_fade_end   DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_end        DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_hook_in    DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_hook_fade  DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_hook_out   DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_loop_in    DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_loop_out   DOUBLE NULL,
            ADD COLUMN IF NOT EXISTS cue_anchor     DOUBLE NULL;
        """;
}
