namespace RDM.Infrastructure.Database;

public sealed class Migration_42_0_0_AddEncoderArmedAndTitles : IMigration
{
    public string Version     => "42.0.0";
    public string Description => "Split encoder 'armed' from 'auto_start' and add stream title mode";

    /// <summary>
    /// <c>armed</c> and <c>auto_start</c> answer two different questions that were previously
    /// conflated: <c>armed</c> means "the bottom-bar button starts this profile", while
    /// <c>auto_start</c> means "and it also starts by itself when the application launches".
    /// Every existing profile that had auto_start set was, under the old meaning, one the operator
    /// wanted on air — so it is armed as well, and the backfill below preserves that intent.
    ///
    /// <c>title_mode</c> decides what metadata the encoder pushes to the cast server:
    /// NOW_PLAYING (formatted from the track, reusing the Stream Titles format), STATIC (a fixed
    /// string in <c>title_text</c>), or NONE.
    /// </summary>
    public string UpSql => """
        ALTER TABLE encoder_profiles
            ADD COLUMN IF NOT EXISTS armed      TINYINT(1)   NOT NULL DEFAULT 0 AFTER auto_start,
            ADD COLUMN IF NOT EXISTS title_mode VARCHAR(16)  NOT NULL DEFAULT 'NOW_PLAYING' AFTER armed,
            ADD COLUMN IF NOT EXISTS title_text VARCHAR(255) NULL AFTER title_mode;

        UPDATE encoder_profiles SET armed = 1 WHERE auto_start = 1;
        """;
}
